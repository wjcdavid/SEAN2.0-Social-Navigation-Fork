# Fix: NullReferenceException in `MTrialInfo.SerializationStatements`

## Symptom

The ROS connection thread crashes repeatedly with:

```
Connection to 127.0.0.1:10000 failed - System.NullReferenceException: Object reference not set to an instance of an object
  at RosMessageTypes.SocialSimRos.MTrialInfo.SerializationStatements () in .../MTrialInfo.cs:132
  at Unity.Robotics.ROSTCPConnector.ROSConnection.WriteDataStaggered (...) in ROSConnection.cs:585
  at Unity.Robotics.ROSTCPConnector.ROSConnection+<ConnectionThread>d__75.MoveNext () in ROSConnection.cs:393
```

`MTrialInfo.cs:132` is the body of the `robot_poses` serialization loop:

```csharp
foreach (var entry in robot_poses)
    listOfSerializations.Add(entry.Serialize());   // entry is null -> NRE
```

## Root cause: a threading data race (not bad data)

This is **not** a case of bad input data. `Util.Geometry.GetMPose(...)` always returns a
non-null `MPose`, so no null element is ever produced at construction time.

The real cause is that a single, shared message instance is mutated on the main thread
while it is serialized on a background thread.

`ROSConnection.Send()` does **not** serialize the message. It only enqueues the
*reference*:

```csharp
public void Send(string rosTopicName, Message message) {
    m_OutgoingMessages.Enqueue(new Tuple<string, Message>(rosTopicName, message));
    ...
}
```

The actual serialization runs **later, on the background `ConnectionThread`**
(`ConnectionThread` -> `WriteDataStaggered` -> `MTrialInfo.SerializationStatements`), which
matches the stack trace.

`MetricsPublisher` (before the fix) kept **one** `MTrialInfo` instance, allocated once in
`Start()`, and re-filled it every frame in `Update()`:

```csharp
trialInfoMessage.robot_poses = new RosMessageTypes.Geometry.MPose[count]; // all elements null
for (int i = 0; i < count; i++)
{
    trialInfoMessage.robot_poses[i] = Util.Geometry.GetMPose(...); // filled one by one
    ...
}
...
ros.Send(TopicName, trialInfoMessage);
```

Two threads then touch the same object concurrently:

- **Connection thread** is iterating `robot_poses` and calling `entry.Serialize()`.
- **Main thread** runs the next `Update()` and reassigns `robot_poses = new MPose[count]`,
  whose elements are **all `null`** until the `for` loop fills them.

If the connection thread reads an element the main thread has not filled yet, `entry` is
`null` and `entry.Serialize()` throws. Because the message is published every frame, the
read/write windows overlap constantly — hence the error happening "all the time."

The `Connection to 127.0.0.1:10000 failed` line is a *symptom*: the unhandled exception
tears down the send loop. It is not a separate networking problem and stops once the NRE is
gone.

## Fix

Never mutate a message after handing it to `Send()`. Treat a published message as immutable,
because serialization is deferred to the background thread.

The simplest correct fix is to build a **fresh** `MTrialInfo` on each publish, so the object
the connection thread serializes is fully populated and never touched again.

`Assets/Scripts/SEAN/Metrics/MetricsPublisher.cs`:

```csharp
void Start()
{
    ros = ROSConnection.instance;
    sean = SEAN.instance;
}

private void Update()
{
    if (!sean.robotTask.isRunning) { return; }

    // Build a fresh message each publish. ROSConnection.Send() only enqueues
    // the reference and serializes it later on the background connection thread,
    // so mutating a shared/reused instance here races with that serialization
    // (e.g. a freshly reallocated robot_poses array whose elements are still
    // null) and throws NullReferenceException in SerializationStatements().
    var trialInfoMessage = new RosMessageTypes.SocialSimRos.MTrialInfo();
    sean.clock.UpdateMHeader(trialInfoMessage.header);

    // ... populate fields ...

    ros.Send(TopicName, trialInfoMessage);
}
```

The previously shared `private RosMessageTypes.SocialSimRos.MTrialInfo trialInfoMessage;`
field is removed; `trialInfoMessage` is now a local that lives for exactly one publish.

## Trade-off

One extra small object allocation per frame. This is negligible here — the `robot_poses`
and `robot_poses_ts` arrays were already reallocated every frame, so the wrapper object adds
trivial GC pressure. To reduce both allocations and topic volume, publish at a fixed lower
rate (e.g. a timer / `InvokeRepeating`) instead of every `Update()`.

## General rule / where else to check

Any publisher that keeps a single `Message` field and re-fills it each frame before
`ros.Send()` is vulnerable to the same race. The bug surfaces most easily on messages with
variable-length arrays (like `MTrialInfo`), but fixed-size messages can also tear. When
auditing `*Publisher.cs` scripts, prefer one of:

- allocate a fresh message per publish (as done here), or
- double-buffer, or
- only ever mutate the message on the same thread that serializes it.
