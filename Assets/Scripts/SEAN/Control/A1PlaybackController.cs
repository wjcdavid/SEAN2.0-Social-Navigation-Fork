// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SEAN.Control
{
    class MocapFrame
    {
        public static string[] JOINTS = {
            "FR_hip",
            "FR_thigh",
            "FR_calf",
            "FL_hip",
            "FL_thigh",
            "FL_calf",
            "RR_hip",
            "RR_thigh",
            "RR_calf",
            "RL_hip",
            "RL_thigh",
            "RL_calf",
        };

        public int frame;
        public int t;
        public float[] joints;

        public MocapFrame(string csvLine)
        {
            var tokens = csvLine.Split(',');
            frame = int.Parse(tokens[0]);
            t = int.Parse(tokens[1]);
            joints = new float[JOINTS.Length];
            for (int i = 0; i < JOINTS.Length; i++)
            {
                joints[i] = float.Parse(tokens[i + 2]) * (180 / Mathf.PI);
            }
        }
    }

    public class A1PlaybackController : MonoBehaviour
    {
        // Joints actually driven by the mocap, paired with their column index in
        // the csv (== index in MocapFrame.JOINTS).
        private struct DrivenJoint
        {
            public ArticulationBody body;
            public int jointIndex;
        }
        private List<DrivenJoint> drivenJoints;
        private List<MocapFrame> frames;

        // PD position-drive gains applied to every actuated joint. This was the
        // bug: the imported drives ship with stiffness/damping = 0, so the position
        // drive produces no torque, the legs go limp and the robot collapses on Play.
        public float stiffness = 1000f;
        public float damping = 50f;
        public float forceLimit = 500f;

        // Advance one mocap frame every this many Update calls. Lower = faster playback.
        public int updateFrequency = 60;
        private int updateCount = 0;
        private int currentFrame = 0;

        void Awake()
        {
            var sr = new StreamReader(Application.dataPath + @"/Resources/a1mocap.csv");
            string line;
            frames = new List<MocapFrame>();
            while ((line = sr.ReadLine()) != null)
            {
                //print(line);
                frames.Add(new MocapFrame(line));
            }
        }

        void Start()
        {
            // Configure each actuated joint's position drive. Without non-zero
            // stiffness/damping the joints cannot hold any target and the robot
            // just flops to the ground. We also seed the first mocap frame as the
            // target so the robot stands the instant Play is pressed.
            //
            // NOTE: we intentionally do NOT add the URDF-importer JointControl
            // component here. It calls Controller.UpdateControlType() on the
            // (disabled) Controller on the root, which writes stiffness = damping = 0
            // back onto every drive and undoes the gains set below.
            drivenJoints = new List<DrivenJoint>();
            foreach (ArticulationBody joint in GetComponentsInChildren<ArticulationBody>())
            {
                if (joint.jointType == ArticulationJointType.FixedJoint)
                {
                    continue;
                }

                ArticulationDrive drive = joint.xDrive;
                drive.stiffness = stiffness;
                drive.damping = damping;
                drive.forceLimit = forceLimit;

                int jointIndex = System.Array.IndexOf(MocapFrame.JOINTS, joint.name);
                if (jointIndex >= 0)
                {
                    if (frames.Count > 0)
                    {
                        drive.target = frames[0].joints[jointIndex];
                    }
                    drivenJoints.Add(new DrivenJoint { body = joint, jointIndex = jointIndex });
                }

                joint.xDrive = drive;
            }
        }

        private void Update()
        {
            updateCount++;
            // Advance one mocap frame every `updateFrequency` calls. (The original
            // `updateFrequency % updateCount` was inverted: it advanced on the
            // divisors of 60 and then froze playback forever once updateCount > 60.)
            if (frames.Count == 0 || updateCount % updateFrequency != 0)
            {
                return;
            }

            currentFrame %= frames.Count;
            MocapFrame frame = frames[currentFrame];
            foreach (DrivenJoint driven in drivenJoints)
            {
                // Map each joint to its own csv column by name rather than by
                // enumeration order, so angles can't be assigned to the wrong leg.
                RotateTo(driven.body, frame.joints[driven.jointIndex]);
            }
            currentFrame++;
        }

        void RotateTo(ArticulationBody articulation, float primaryAxisRotation)
        {
            var drive = articulation.xDrive;
            drive.target = primaryAxisRotation;
            articulation.xDrive = drive;
        }
    }
}
