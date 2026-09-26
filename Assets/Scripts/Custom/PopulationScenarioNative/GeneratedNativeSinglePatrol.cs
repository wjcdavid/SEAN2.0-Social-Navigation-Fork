using System.Collections;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    // Patrol destinations only. SFAgent still computes movement; the native player owns gestures.
    [DefaultExecutionOrder(-8500)]
    public sealed class GeneratedNativeSinglePatrol : MonoBehaviour
    {
        private Base agent;
        private PedestrianModulator mod;
        private IVI.SFAgent sf;
        private Transform robot;
        private Vector3 a, b;
        private bool targetIsB = true, ready, reactionOwned, assertive, assertiveNear;
        private float trigger, release, previousDistance = float.PositiveInfinity;
        private float bestDistance = float.PositiveInfinity, originalRepulsion;
        public void Configure(Base source, PedestrianModulator modulator, Transform target,
            Vector3 start, Vector3 end, float triggerDistance, float releaseDistance)
        {
            agent = source; mod = modulator; robot = target; a = start; b = end;
            trigger = triggerDistance; release = Mathf.Max(trigger + 0.01f, releaseDistance);
            assertive = mod.personality == PedestrianModulator.PersonalityType.Assertive;
            if (assertive) mod.personality = PedestrianModulator.PersonalityType.Indifferent;
        }
        IEnumerator Start()
        {
            // InitDest requires Base.Start/INavigable.Start to have finished.
            yield return null;
            if (!agent || !mod) { enabled = false; yield break; }
            sf = agent.GetComponent<IVI.SFAgent>();
            if (sf) originalRepulsion = sf.RobotRepulsion;
            ready = true;
            Resume();
        }
        void Update()
        {
            if (!ready || !agent || !mod) return;
            if (assertive) {
                float distance = robot ? FlatDistance(robot.position, transform.position) : float.PositiveInfinity;
                if (!assertiveNear && distance <= trigger) assertiveNear = true;
                if (assertiveNear && distance > release) assertiveNear = false;
                mod.personality = assertiveNear ? PedestrianModulator.PersonalityType.Assertive : PedestrianModulator.PersonalityType.Indifferent;
                if (sf) sf.RobotRepulsion = assertiveNear ? mod.assertiveRobotRepulsion : originalRepulsion;
            }
            bool owns = mod.IsControllingDestination || mod.IsRotationSuppressed();
            if (owns) { reactionOwned = true; ResetArrival(); return; }
            if (reactionOwned) { reactionOwned = false; Resume(); }
            float d = FlatDistance(transform.position, targetIsB ? b : a);
            if (d <= 0.55f || (bestDistance <= 1f && d > previousDistance + 0.05f)) {
                targetIsB = !targetIsB;
                Resume();
            } else { bestDistance = Mathf.Min(bestDistance, d); previousDistance = d; }
        }
        void Resume() { agent.InitDest(targetIsB ? b : a); ResetArrival(); }
        void ResetArrival() { previousDistance = bestDistance = float.PositiveInfinity; }
        static float FlatDistance(Vector3 x, Vector3 y) { x.y = y.y = 0; return Vector3.Distance(x, y); }
        void OnDisable() { if (ready && assertive && sf) sf.RobotRepulsion = originalRepulsion; }
    }
}
