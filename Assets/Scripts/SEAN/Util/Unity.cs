// Copyright (c) 2021, Members of Yale Interactive Machines Group, Yale University,
// Nathan Tsoi
// All rights reserved.
// This source code is licensed under the BSD-style license found in the
// LICENSE file in the root directory of this source tree.

using UnityEngine;

namespace SEAN.Util
{
    public class Unity
    {
        // from: https://answers.unity.com/questions/458207/copy-a-component-at-runtime.html
        public static T CopyComponent<T>(T original, GameObject destination)
            where T : Component
        {
            if (original == null)
                throw new System.ArgumentNullException(nameof(original));

            if (destination == null)
                throw new System.ArgumentNullException(nameof(destination));

            if (original is Camera sourceCamera)
            {
                Camera copiedCamera = destination.AddComponent<Camera>();
                copiedCamera.CopyFrom(sourceCamera);
                return copiedCamera as T;
            }

            throw new System.ArgumentException(
                "CopyComponent: Unsupported type: " + original.GetType());
        }
    }
}
