using System;
using System.Collections.Generic;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Adds small body CapsuleColliders to generated visible pedestrians for RL collision detection.
    /// This is a non-social-force utility: social-force proxies remain controlled by each group spawner.
    /// Attach to PedestrianControl or to a specific group root.
    /// </summary>
    public class VisiblePersonBodyColliderInstaller : MonoBehaviour
    {
        [Header("Install")]
        public bool installOnStart = true;
        public bool keepInstallingForNewGeneratedPeople = true;
        public bool logInstallDetails = false;

        [Header("Which Generated People")]
        public bool includeStaticGroupMembers = true;
        public bool includeDynamicAttentionMembers = true;
        public bool includeMovingGroupMembers = true;
        public List<Transform> explicitPeopleOrRoots = new List<Transform>();

        [Header("Body Collider")]
        public bool colliderIsTrigger = true;

        [Min(0.01f)]
        public float radius = 0.3f;

        [Min(0.1f)]
        public float height = 1.7f;

        [Min(0f)]
        public float centerY = 0.85f;

        public LayerMask contactMask = ~0;
        public string optionalLayerName = string.Empty;

        [Header("Runtime Debug")]
        [SerializeField]
        private int installedColliderCount;

        [SerializeField]
        private int activeContactCount;

        private readonly HashSet<Collider> activeContacts = new HashSet<Collider>();
        private readonly HashSet<Transform> installedRoots = new HashSet<Transform>();

        public int InstalledColliderCount
        {
            get { return installedColliderCount; }
        }

        public int ActiveContactCount
        {
            get { return activeContactCount; }
        }

        private void OnValidate()
        {
            radius = Mathf.Max(0.01f, radius);
            height = Mathf.Max(0.1f, height);
            centerY = Mathf.Max(0f, centerY);
        }

        private void Start()
        {
            if (installOnStart)
            {
                InstallNow();
            }
        }

        private void LateUpdate()
        {
            if (keepInstallingForNewGeneratedPeople)
            {
                InstallNow();
            }
        }

        [ContextMenu("Install Visible Person Body Colliders Now")]
        public void InstallNow()
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (IsGeneratedPersonRoot(t))
                {
                    InstallOnPersonRoot(t);
                }
            }

            if (explicitPeopleOrRoots != null)
            {
                for (int i = 0; i < explicitPeopleOrRoots.Count; i++)
                {
                    Transform root = explicitPeopleOrRoots[i];
                    if (root == null)
                    {
                        continue;
                    }

                    if (HasPersonLikeRendererOrAnimator(root))
                    {
                        InstallOnPersonRoot(root);
                    }

                    Transform[] children = root.GetComponentsInChildren<Transform>(true);
                    for (int c = 0; c < children.Length; c++)
                    {
                        if (IsGeneratedPersonRoot(children[c]))
                        {
                            InstallOnPersonRoot(children[c]);
                        }
                    }
                }
            }
        }

        private bool IsGeneratedPersonRoot(Transform t)
        {
            if (t == null)
            {
                return false;
            }

            string n = t.name;
            if (includeStaticGroupMembers && n.StartsWith("StaticGroupMember_", StringComparison.Ordinal))
            {
                return true;
            }

            if (includeDynamicAttentionMembers && n.StartsWith("DynamicAttentionMember_", StringComparison.Ordinal))
            {
                return true;
            }

            if (includeMovingGroupMembers && n.StartsWith("MovingGroupMember_", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private bool HasPersonLikeRendererOrAnimator(Transform root)
        {
            if (root == null)
            {
                return false;
            }

            if (root.GetComponentInChildren<Animator>(true) != null)
            {
                return true;
            }

            return root.GetComponentInChildren<Renderer>(true) != null;
        }

        private void InstallOnPersonRoot(Transform personRoot)
        {
            if (personRoot == null || installedRoots.Contains(personRoot))
            {
                return;
            }

            if (personRoot.GetComponent<VisiblePersonBodyCollisionReporter>() != null ||
                personRoot.GetComponent("MovingSocialGroupPersonBodyReporterV5") != null ||
                personRoot.GetComponent("MovingSocialGroupPersonBodyReporter_WJC") != null ||
                personRoot.GetComponent("DynamicMovingAttentionGroupPersonBodyReporter_WJC_SAFE") != null)
            {
                installedRoots.Add(personRoot);
                installedColliderCount = installedRoots.Count;
                return;
            }

            int requestedLayer = -1;
            if (!string.IsNullOrEmpty(optionalLayerName))
            {
                requestedLayer = LayerMask.NameToLayer(optionalLayerName);
                if (requestedLayer < 0)
                {
                    Debug.LogWarning(
                        "[VisiblePersonBodyColliderInstaller] Layer '" + optionalLayerName +
                        "' does not exist. Keeping existing layer.",
                        this);
                }
            }

            if (requestedLayer >= 0)
            {
                personRoot.gameObject.layer = requestedLayer;
            }

            CapsuleCollider capsule = personRoot.gameObject.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = radius;
            capsule.height = Mathf.Max(height, radius * 2f);
            capsule.center = new Vector3(0f, centerY, 0f);
            capsule.isTrigger = colliderIsTrigger;

            Rigidbody rb = personRoot.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = personRoot.gameObject.AddComponent<Rigidbody>();
            }
            rb.isKinematic = true;
            rb.useGravity = false;

            VisiblePersonBodyCollisionReporter reporter =
                personRoot.gameObject.AddComponent<VisiblePersonBodyCollisionReporter>();
            reporter.Initialize(this);

            installedRoots.Add(personRoot);
            installedColliderCount = installedRoots.Count;

            if (logInstallDetails)
            {
                Debug.Log(
                    "[VisiblePersonBodyColliderInstaller] Added body collider to " + personRoot.name,
                    this);
            }
        }

        internal void RegisterEnter(Collider other)
        {
            if (ShouldIgnore(other))
            {
                return;
            }

            activeContacts.Add(other);
            activeContactCount = activeContacts.Count;
        }

        internal void RegisterExit(Collider other)
        {
            if (other == null)
            {
                return;
            }

            activeContacts.Remove(other);
            activeContactCount = activeContacts.Count;
        }

        private bool ShouldIgnore(Collider other)
        {
            if (other == null)
            {
                return true;
            }

            if (other.GetComponent<GroupReactionSFAgent>()) return true;
            var reactionBody = other.GetComponentInParent<GroupReactionSFAgent>();
            if (reactionBody && reactionBody.bridge && reactionBody.bridge.Owner &&
                reactionBody.bridge.Owner.transform.IsChildOf(transform)) return true;

            if ((contactMask.value & (1 << other.gameObject.layer)) == 0)
            {
                return true;
            }

            if (other.transform.IsChildOf(transform))
            {
                // Ignore contacts within the same installer root unless your robot is also under this root.
                // Put the robot outside PedestrianControl for normal use.
                return true;
            }

            return false;
        }
    }

    public class VisiblePersonBodyCollisionReporter : MonoBehaviour
    {
        private VisiblePersonBodyColliderInstaller owner;

        public void Initialize(VisiblePersonBodyColliderInstaller installer)
        {
            owner = installer;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (owner != null)
            {
                owner.RegisterEnter(other);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (owner != null)
            {
                owner.RegisterExit(other);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (owner != null && collision != null)
            {
                owner.RegisterEnter(collision.collider);
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (owner != null && collision != null)
            {
                owner.RegisterExit(collision.collider);
            }
        }
    }
}
