# SEAN 2.0 Social Navigation Extensions

This project adds pedestrian behaviors and population generation to SEAN 2.0.
You can create a scene with moving people, stationary people, social groups,
and special populations from the Unity Inspector.

The extended version is on the **`jiacheng/local-version`** branch.
The setup below uses **Windows and PowerShell**, the environment used to
prepare and upload this version.

## 1. Clone the project on Windows

Install Git for Windows and Unity Hub first. Open PowerShell in the folder
where you want to keep the project, then run:

```powershell
git clone --branch jiacheng/local-version --single-branch "https://github.com/wjcdavid/SEAN2.0-Social-Navigation-Robot.git"
cd "SEAN2.0-Social-Navigation-Robot"
```

Keep `--branch jiacheng/local-version` in the command so you get the extended
version, even if the repository's default branch changes.

For this branch's current snapshot:

- Git LFS is not required. The large FBX files are stored as regular Git files.
- Rocketbox assets are included directly. No submodule initialization is needed.
- The project is large, so allow time and disk space for cloning and importing.

## 2. Open the project in Unity

1. Install **Unity 2022.3.40f1** through Unity Hub. The project version is also
   recorded in [`ProjectSettings/ProjectVersion.txt`](ProjectSettings/ProjectVersion.txt).
2. In Unity Hub, add the cloned project folder and open it with that version.
3. Wait for Unity to finish importing assets and compiling scripts.
4. Open **`Assets/Scenes/SEAN/Outdoor.unity`** from the Project window.

Keep the scene's SEAN system and the robot you want to use active. If you use
ROS-based robot control, configure ROS separately using the
[official SEAN documentation](https://sean.interactive-machines.com/).

## 3. Set up the population generator

If the scene already has a **Population Scenario Generator**, select that
object. Otherwise:

1. In the Hierarchy, choose **Create Empty** at the scene root.
2. Name the object **PopulationScenario**.
3. In its Inspector, click **Add Component** and search for
   **Population Scenario Generator**.

For the first test, leave **Generate On Start** unchecked. Keep
**Clear Before Generate** checked so generating again replaces the previous
population created by this generator.

Disable other demo spawners you are not using if they also create people.

### Connect the robot and assets

- Drag the target robot's **trunk**, base, or body Transform into **Robot**.
  For Unitree A1, this is usually `SEAN/Robots/Unitree A1/base/trunk`.
- Click **1. Auto-Fill Known Project Assets** at the bottom of the Inspector.
  This fills the known character prefabs and built-in animation references.
- Keep **Rocketbox Resources Path** as `Prefabs/Rocketbox`. This is a path
  relative to `Assets/Resources`, not a Windows folder path.
- Check the Console for missing-asset messages before continuing.

### Choose where people can appear

The generator uses a rectangular area over the scene's baked **NavMesh**
(the walkable surface shown in blue when NavMesh display is enabled).

| Inspector setting | What it does |
| --- | --- |
| Local Area Center | Places the generation area relative to the generator object. |
| Area Size | Sets the area's width and depth on the local X/Z plane. The second field is Z depth, even if Unity labels it Y. |
| Draw Generation Area | Shows the selected area in the Scene view. |
| Constrain Nav Mesh To Area Height | Rejects NavMesh surfaces on the wrong floor or at the wrong height. Keep this enabled. |
| Nav Mesh Height Tolerance | Sets the allowed height difference from the area center. |
| Minimum Clearance | Leaves space between generated people and groups. |
| Patrol Minimum / Maximum Distance | Sets the range used to choose moving people's A/B routes. |

A simple setup is to move the generator object onto the intended sidewalk,
set **Local Area Center** to `(0, 0, 0)`, and adjust **Area Size** around it.
Keep the object's rotation at `(0, 0, 0)` and scale at `(1, 1, 1)` for easy setup.
The area must overlap a baked NavMesh at the correct height.

## 4. Choose the population and generate it

### Set the counts

| Inspector setting | Meaning |
| --- | --- |
| Requested Regular Population | Total ordinary people, including people inside groups. |
| Requested Special Population | Extra special people, added separately as moving individuals. |
| Moving Single Weight | Share of ordinary people who move alone. |
| Moving Group Weight | Share of ordinary people who belong to moving groups. |
| Static Single Weight | Share of ordinary people who stand alone. |
| Static Group Weight | Share of ordinary people who belong to stationary groups. |
| Personality Probability | Controls how often the randomizer assigns a personal reaction. |
| Special Walk Probability | Chance of a special walking style for an ordinary moving individual. |
| Group Reaction Probability | Chance that a group receives a whole-group reaction. |
| Random Seed | Use a nonzero number to repeat the random configuration. Use `0` for a new random seed. |

The four weights divide the **ordinary headcount**, not the number of groups.
The generator normalizes them and adjusts allocations to fit valid group sizes.
Special people are added on top of that headcount.

Start with a small population, such as **8 regular people and 0 special people**.
Once that works, increase the counts and add special people. A small generation
area may not have enough room for every requested person or route.

### Use the buttons in this order

1. **Before Play:** click **1. Auto-Fill Known Project Assets**.
2. **Before Play:** set your area, counts, weights, and probabilities.
3. **Before Play:** click **2. Randomize Configuration**.
4. Review **Moving Singles**, **Static Singles**, **Moving Groups**, and
   **Static Groups**. You can edit individual entries here.
5. Save the scene to keep the generator settings.
6. Click Unity's **Play** button and wait for the SEAN robot to initialize.
7. Click **3. Generate Population** in the generator Inspector.

Generated objects appear under **GeneratedPopulation** beneath the generator.
The Console reports how many spawn requests were accepted. Check it if fewer
people appear than expected.

**Randomize Configuration replaces the current configuration lists**, including
manual edits. Configure and save before Play; the generated population itself
is created at runtime.

After the first successful test, you can enable **Generate On Start** before
Play. It generates from the saved configuration automatically; it does not
press Randomize for you. Use **Clear Generated Population** during Play to
remove this generator's current population.

## 5. Walking styles, reactions, and groups

### Ordinary individuals

Moving individuals can use **Original** walking or an available special style:
**Kimodo Relaxed**, **Kimodo Elderly**, **Old Man Walk**, **Drunk Walk**,
**Carry And Walk**, or **Pacing Phone**.

The randomizer checks which walking assets are available. See
**Randomization Report** for the result. Stationary individuals do not patrol.

Use **Personality / 一级反应** to choose `Normal`, `Curious`, `Scared`,
`Surprised`, or `Assertive`. Open **Native Reaction / 二级反应与动画** to choose
the more specific behavior.

For example, Curious can use **Stand And Observe**, **Approach And Follow**,
or **Crouch And Observe**, depending on the person's movement mode and available
assets. Randomization uses built-in reaction choices that do not require you
to supply a Custom Reaction Clip.

If you manually select **Custom Gesture** or enable a gesture before a native
behavior, you must supply a compatible, non-Legacy Humanoid animation clip.
Fixed individuals do not translate toward or away from the robot; use an
observation or gesture when you want a visible stationary reaction.

### Groups

| Group type | Members | Formation choices |
| --- | --- | --- |
| Moving group | 2-3 | Side By Side, V Shape |
| Static group | 3-4 | L Shape, O Shape |

**Group Reaction** controls the whole group. Its choices are **None**,
**Attention**, **Attraction**, and **Split Corridor**.

**Member Personality** and **Native Reaction** control each member's personal
reaction separately. Normal members use Random Rocketbox; reactive members use
the SimpleAppearanceAgent setup. Moving groups keep their existing walking
controllers, so you do not need to assign a separate walking clip to each group.

In this uploaded version, randomization assigns a personal reaction to **zero
or one member per group**. To configure more reactive members, edit their
personality and reaction settings manually after Randomize and before Play.

For moving groups with a whole-group reaction, **Stop For Robot Encounter**
enables the stop-react-resume behavior. **Limit Encounter Duration** adds a
time limit; **Encounter Cooldown Seconds** controls the delay before another
group encounter. Member cooldown is set separately under **Native Reaction**.

Keep **Max Formation Offset** small if reactive members should stay close to
their assigned positions in the formation.

### Special populations

The scene generator supports these eight types as moving individuals:

- Cyclist
- Dog Walker
- Female Child
- Male Child
- Phone User
- Scooter User
- Wheelchair User
- White Cane User

They use **SpecialPopulationSpawner** and their existing model/controller setup.
The scene generator does not apply ordinary walking-style or personality
overrides to them. If you need to place a stationary special person separately,
use the **Special Population Spawner** component and choose **Stationary**.

The included Scooter setup can move in its imported pose when it has no
playable animation. In that case, it does not play a kicking or wheel animation.

## 6. Check the reactions

Move the selected robot near the generated people or groups.

- **Personality Trigger Distance** controls the personal reaction range.
- **Group Trigger Distance** controls the whole-group reaction range.
- Keep each **Reset Distance** larger than its trigger distance. It is a
  distance for releasing/resetting the encounter, not a cooldown timer.

The existing behavior scripts still control their own activation conditions,
including any heading or viewing-angle checks. Distance alone does not mean
every group reaction must activate immediately.

The generator samples placement and patrol routes on the NavMesh. Groups also
have a runtime height correction. This does not replace every behavior with a
NavMeshAgent or guarantee that every reaction stays inside the selected box.

**Add Visible Body Colliders** adds collision helpers. If **Body Colliders Are
Triggers** is checked, those added colliders detect overlap rather than
physically blocking movement.

## 7. Common setup problems

| Problem | What to check |
| --- | --- |
| Generate Population is greyed out | Enter Play mode. Configure and randomize before Play. |
| No people appear | Check the Console, Robot reference, asset references, and baked NavMesh. |
| Selected area does not overlap a NavMesh triangle | Move the area onto the sidewalk and check its Y height and height tolerance. |
| Fewer people appear than requested | Enlarge the walkable area, reduce the counts, or shorten patrol distances. |
| Missing special container or model | Click Auto-Fill, then check the reported prefab and its model references. |
| A custom reaction does not play | Supply the required compatible clip, or choose a built-in reaction. |
| People appear twice | Check whether another demo spawner or generator is also active. |

## Sources and acknowledgements

This project is based on official
[SEAN 2.0](https://github.com/yale-sean/social_sim_unity).

It also includes work from
[`shengqu12/social_sim_unity`](https://github.com/shengqu12/social_sim_unity),
branch `sheng/ped-behavior-v2`, based on commit
`ba5b789578d8523e20cb697c884a4d4fd6814280`, with subsequent local changes by
**Jiacheng Wang**.

The population generator connects the project's existing pedestrian, group,
and special-population scripts. Its main files are in
[`Assets/Scripts/Custom/PopulationScenarioNative`](Assets/Scripts/Custom/PopulationScenarioNative).

Related upstream resources:

- [SEAN documentation](https://sean.interactive-machines.com/)
- [SEAN Unity project](https://github.com/yale-sean/social_sim_unity)
- [SEAN ROS project](https://github.com/yale-sean/social_sim_ros)
- [SEAN Catkin workspace](https://github.com/yale-sean/sim_ws)
- [SEAN documentation source](https://github.com/yale-sean/social_sim_docs)

Original license files and attribution notices are retained. See
[`LICENSE`](LICENSE) and the notices included with third-party assets.
