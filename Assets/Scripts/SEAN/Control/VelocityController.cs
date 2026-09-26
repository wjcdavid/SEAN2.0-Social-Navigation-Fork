// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;

namespace SEAN.Control
{
    public class VelocityController : ControlSubscriber
    {
        private Rigidbody rb;
        // Articulation-based robots (e.g. the Unitree A1 imported from URDF) move
        // through their root ArticulationBody, which PhysX simulates in world space
        // and does NOT follow its parent Transform. Driving the base_link Rigidbody
        // alone therefore leaves the visible robot standing still, so when a root
        // articulation exists we drive that instead.
        private ArticulationBody artRoot;
        // The articulated root is a free body with no balance controller. We keep it
        // upright purely by setting its angular velocity (a stable, solver-friendly
        // operation), correcting any tilt over this many seconds. We deliberately do
        // NOT use TeleportRoot / immovable here: on this Rigidbody-parented hierarchy
        // those make the solver fly/tremble.
        public float uprightResponse = 0.15f;
        public float maxUprightAngSpeed = 15f;
        // Softly hold the body at its standing height (via vertical velocity, never
        // TeleportRoot) so foot contacts can't make it bounce on spawn or float while
        // skidding along. The target height is the ground directly beneath the robot
        // (ray-cast each step) plus standingHeightOffset, so it stands correctly in any
        // scene / on uneven terrain instead of at a single height captured at spawn.
        public float heightResponse = 0.2f;
        public float maxVerticalSpeed = 2f;
        public float standingHeightOffset = 0.30f;
        public float groundRayLength = 5f;
        private Transform robotRoot;
        // Whether the current command came from the keyboard, so we can stop the
        // instant the keys are released instead of coasting for maxTimeDeltaSec.
        private bool keyboardActive;

        // When a task starts the robot is (re)placed at the start pose; give it this
        // long to stand up (legs extend, body levels and settles to height) before it
        // is allowed to move, so it doesn't drive off mid-crouch in a bad posture.
        public float settleSeconds = 1.5f;
        private float settleUntil = 0f;
        private bool taskWasRunning = false;

        private float targetLinVelocity, targetAngVelocity;
        public float maxTimeDeltaSec = 0.25f;
        private float lastMessageTS = 0;

        // PID Controller
        public float P = 1, I = 1, D = 1;
        private float integral, lastError;

        protected void Start()
        {
            base.Start();
            rb = sean.robot.base_link.GetComponent<Rigidbody>();
            artRoot = FindArticulationRoot(sean.robot.base_link);
            robotRoot = sean.robot.gameObject.transform;
            if (artRoot != null)
            {
                DisableLegColliders();
            }
            // ROSConnection.instance.Subscribe<RosMessageTypes.Geometry.MTwist>(Topic, CmdVelMessage);
        }

        // The legs are cosmetic (mocap animation) and the body is held above the
        // ground by ray-cast, not by the legs. Their ground contacts therefore only
        // fight the kinematic body control and pitch/splay the body, so we disable the
        // leg-link colliders. The trunk's own collider (and the base capsule, which is
        // not under the articulation root) are left intact.
        private void DisableLegColliders()
        {
            foreach (Collider col in artRoot.GetComponentsInChildren<Collider>())
            {
                if (col.GetComponentInParent<ArticulationBody>() != artRoot)
                {
                    col.enabled = false;
                }
            }
        }

        private static ArticulationBody FindArticulationRoot(GameObject robotBase)
        {
            foreach (ArticulationBody body in robotBase.GetComponentsInChildren<ArticulationBody>())
            {
                if (body.isRoot)
                {
                    return body;
                }
            }
            return null;
        }

        // Desired body height = the ground directly under the robot + standing offset.
        // Ray-cast downward, skipping the robot's own colliders and triggers, so this
        // works in any scene and on uneven terrain. Falls back to the current height
        // if nothing is found (e.g. robot is off the edge of the world).
        private float GroundTargetHeight()
        {
            Vector3 origin = artRoot.transform.position + Vector3.up * 0.2f;
            float nearest = float.PositiveInfinity;
            float targetY = artRoot.transform.position.y;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, groundRayLength + 0.2f))
            {
                if (hit.collider.isTrigger)
                {
                    continue;
                }
                if (robotRoot != null && hit.collider.transform.IsChildOf(robotRoot))
                {
                    continue;
                }
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    targetY = hit.point.y + standingHeightOffset;
                }
            }
            return targetY;
        }

        private void Update()
{
    // Check for WASD input
            float moveHorizontal = 0.0f;
            float moveVertical = 0.0f;

            if (UnityEngine.Input.GetKey(KeyCode.A))
            {
                moveHorizontal = -1.0f; // Move left
            }
            else if (UnityEngine.Input.GetKey(KeyCode.D))
            {
                moveHorizontal = 1.0f; // Move right
            }

            if (UnityEngine.Input.GetKey(KeyCode.W))
            {
                moveVertical = 1.0f; // Move forward
            }
            else if (UnityEngine.Input.GetKey(KeyCode.S))
            {
                moveVertical = -1.0f; // Move backward
            }

            if (moveHorizontal != 0 || moveVertical != 0)
            {
                // Override target velocities with keyboard input
                targetLinVelocity = moveVertical * 1.0f; // m/s
                targetAngVelocity = moveHorizontal * 1.0f; // rad/s
                lastMessageTS = Time.time; // Reset message timestamp to prevent ROS override
                keyboardActive = true;
            }
            else if (keyboardActive)
            {
                // Keys released: stop immediately rather than coasting for
                // maxTimeDeltaSec (which otherwise looks like drifting).
                targetLinVelocity = targetAngVelocity = 0;
                keyboardActive = false;
            }
}


        private void FixedUpdate()
        {
            // Detect a task (re)start -> the robot was just placed; hold still for a
            // moment so it can stand up before moving.
            bool taskRunning = sean != null && sean.robotTask != null && sean.robotTask.isRunning;
            if (taskRunning && !taskWasRunning)
            {
                settleUntil = Time.time + settleSeconds;
            }
            taskWasRunning = taskRunning;

            // Stop while still settling into a stand, or if we haven't heard a command
            // (ROS or keyboard) recently. The body still levels / holds height below.
            if (Time.time < settleUntil || Time.time - lastMessageTS > maxTimeDeltaSec)
            {
                targetAngVelocity = targetLinVelocity = 0;
            }

            if (artRoot != null)
            {
                DriveArticulation();
            }
            else if (rb != null)
            {
                DriveRigidbody();
            }
        }

        // Wheeled / Rigidbody robots (Kuri, etc.): the whole robot is rigidly
        // attached to base_link, so driving its Rigidbody moves everything.
        private void DriveRigidbody()
        {
            rb.angularVelocity = targetAngVelocity == 0.0f
                ? Vector3.zero
                : new Vector3(0, -1 * targetAngVelocity, 0);

            rb.velocity = targetLinVelocity == 0.0f
                ? new Vector3(0, rb.velocity.y, 0)
                : rb.transform.forward * targetLinVelocity;
        }

        // Legged / ArticulationBody robots (Unitree A1): the root is a free-floating
        // body with no balance controller. It stands stably on its stiff legs as long
        // as nothing disturbs it, so we keep it fully DYNAMIC and only ever change its
        // velocities (never TeleportRoot / immovable, which destabilised it). Pushing
        // it horizontally alone would tip it, so every step we also set the angular
        // velocity that rotates it back to level, plus a gentle vertical velocity that
        // holds its standing height so it neither bounces on spawn nor floats away.
        private void DriveArticulation()
        {
            // Angular velocity that cancels any accumulated pitch/roll without
            // touching yaw. We derive it from the body's up-vector: the axis that
            // rotates "up" back to world-up is up x worldUp = (-up.z, 0, up.x), which
            // always has a zero Y (yaw) component. (Deriving it from euler yaw instead
            // leaks a tiny yaw term and makes the robot slowly spin while idle.)
            Vector3 up = artRoot.transform.up;
            Vector3 levelAxis = Vector3.Cross(up, Vector3.up);
            float sinTilt = levelAxis.magnitude;
            Vector3 levelAngVel = Vector3.zero;
            if (sinTilt > 1e-5f)
            {
                float tilt = Mathf.Atan2(sinTilt, Vector3.Dot(up, Vector3.up));
                levelAngVel = (levelAxis / sinTilt) * (tilt / uprightResponse);
                levelAngVel = Vector3.ClampMagnitude(levelAngVel, maxUprightAngSpeed);
            }

            // Heading (yaw only) used to drive horizontal motion.
            Quaternion upright = Quaternion.Euler(0f, artRoot.transform.eulerAngles.y, 0f);

            // Vertical velocity that softly returns the body to its standing height
            // above the ground beneath it, damping the spawn bounce and stopping it
            // from floating up while moving (and letting it follow uneven terrain).
            float dy = GroundTargetHeight() - artRoot.transform.position.y;
            float vy = Mathf.Clamp(dy / heightResponse, -maxVerticalSpeed, maxVerticalSpeed);

            bool moving = targetLinVelocity != 0.0f || targetAngVelocity != 0.0f;

            if (!moving)
            {
                // Stand: kill horizontal drift, hold height, keep leveling.
                artRoot.velocity = new Vector3(0f, vy, 0f);
                artRoot.angularVelocity = levelAngVel;
                return;
            }

            // Yaw command about world up, on top of the leveling correction.
            artRoot.angularVelocity = levelAngVel + new Vector3(0f, -1 * targetAngVelocity, 0f);

            // Drive horizontally along the (level) heading; hold the standing height.
            Vector3 forward = upright * Vector3.forward;
            artRoot.velocity = new Vector3(forward.x * targetLinVelocity, vy, forward.z * targetLinVelocity);
        }

        override sealed protected void CmdVelMessage(RosMessageTypes.Geometry.MTwist msg)
        {
            // print("in callback message");
            if (msg == null) { return; }
            if (rb == null && artRoot == null) { return; }
            targetLinVelocity = (float)msg.linear.x;
            targetAngVelocity = (float)msg.angular.z;
            lastMessageTS = Time.time;
        }

        private float Pid(float setpoint, float actual, float timeFrame)
        {
            float present = setpoint - actual;
            integral += present * timeFrame;
            float deriv = (present - lastError) / timeFrame;
            lastError = present;
            return present * P + integral * I + deriv * D;
        }
    }
}
