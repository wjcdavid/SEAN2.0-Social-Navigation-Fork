// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree. 

using UnityEngine;

namespace SEAN.Scenario
{
    public class Robot : MonoBehaviour
    {
        public float radius = 0.16f;
        public GameObject base_link;
        public Camera camera_first;
        public Camera camera_third;
        public Camera camera_overhead;

        public Trajectory.TrackedTrajectory trajectory { get; private set; }
        private void GetOrAttachTrajectory()
        {
            if (trajectory != null) { return; }
            trajectory = gameObject.GetComponent<Trajectory.TrackedTrajectory>();
            if (trajectory == null)
            {
                trajectory = gameObject.AddComponent(typeof(Trajectory.TrackedTrajectory)) as Trajectory.TrackedTrajectory;
                trajectory.mainGameObject = base_link;
            }
        }
        // Resource paths (relative to a Resources folder) of the shared camera
        // prefabs used by robots that don't ship their own third/overhead views.
        private const string thirdPersonCameraResource = "SEAN/Sensors/ThirdPersonCameraParent";
        private const string overheadCameraResource = "SEAN/Sensors/OverheadCamera";

        public void Start()
        {
            GetOrAttachTrajectory();
            ResolveCameras();
            if (camera_first == null)
            {
                throw new System.ArgumentException("A first person camera must be assigned to the robot " + name);
            }
            if (camera_third == null)
            {
                throw new System.ArgumentException("A third person camera must be assigned to the robot " + name);
            }
            if (camera_overhead == null)
            {
                throw new System.ArgumentException("A overhead camera must be assigned to the robot " + name);
            }
        }

        // Some robot prefabs (e.g. Unitree A1) only wire up the first person
        // camera in the Inspector. Rather than fail, fall back to any matching
        // child camera and finally to the shared sensor prefabs that the other
        // robots already use, so the slots are always populated at runtime.
        private void ResolveCameras()
        {
            if (camera_first == null)
            {
                camera_first = FindChildCamera("first");
            }
            if (camera_third == null)
            {
                camera_third = FindChildCamera("third");
                if (camera_third == null)
                {
                    camera_third = InstantiateCameraPrefab(thirdPersonCameraResource);
                }
            }
            if (camera_overhead == null)
            {
                camera_overhead = FindChildCamera("overhead");
                if (camera_overhead == null)
                {
                    camera_overhead = InstantiateCameraPrefab(overheadCameraResource);
                }
            }
        }

        private Camera FindChildCamera(string nameKeyword)
        {
            foreach (Camera camera in GetComponentsInChildren<Camera>(true))
            {
                if (camera.gameObject.name.ToLowerInvariant().Contains(nameKeyword))
                {
                    return camera;
                }
            }
            return null;
        }

        private Camera InstantiateCameraPrefab(string resourcePath)
        {
            GameObject prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab == null)
            {
                Debug.LogWarning("Could not load camera prefab at Resources/" + resourcePath + " for robot " + name);
                return null;
            }
            Transform parent = base_link != null ? base_link.transform : gameObject.transform;
            GameObject instance = Instantiate(prefab, parent, false);
            instance.name = prefab.name;
            return instance.GetComponentInChildren<Camera>(true);
        }
        public new Transform transform
        {
            get
            {
                return base_link.transform;
            }
        }
        public Vector3 position
        {
            get
            {
                return transform.position;
            }
        }
        public Quaternion rotation
        {
            get
            {
                return transform.rotation;
            }
        }
        public override string ToString()
        {
            return gameObject.name;
        }
    }
}