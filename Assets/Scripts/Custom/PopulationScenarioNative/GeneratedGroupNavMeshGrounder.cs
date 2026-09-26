using UnityEngine;
using UnityEngine.AI;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Final visual grounding pass for generated social groups.
    ///
    /// Group spawners preserve formations by moving a shared root. This component runs after
    /// their LateUpdate and corrects only Y, so patrol X/Z, slot geometry, reactions and group
    /// logic remain unchanged. A reference-height guard prevents snapping to rooftop/upper-floor
    /// NavMesh layers that overlap the selected sidewalk in X/Z.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    [DisallowMultipleComponent]
    public sealed class GeneratedGroupNavMeshGrounder : MonoBehaviour
    {
        [SerializeField] private float referenceWorldY;
        [SerializeField, Min(0.1f)] private float heightTolerance = 1.5f;
        [SerializeField, Min(0.1f)] private float sampleDistance = 2f;
        [SerializeField] private bool logFirstCorrection;

        private bool loggedCorrection;

        public void Configure(
            float referenceY,
            float allowedHeightDifference,
            float navMeshSampleDistance,
            bool logCorrection)
        {
            referenceWorldY = referenceY;
            heightTolerance = Mathf.Max(0.1f, allowedHeightDifference);
            sampleDistance = Mathf.Max(0.1f, navMeshSampleDistance);
            logFirstCorrection = logCorrection;
        }

        public void GroundNow()
        {
            Transform[] all = GetComponentsInChildren<Transform>(true);

            // Ground shared formation roots first. Member world positions then inherit the
            // corrected baseline before their individual slope/foot-height pass.
            for (int i = 0; i < all.Length; i++)
            {
                Transform target = all[i];
                if (target != null && IsGeneratedGroupRoot(target.name))
                    GroundTransformY(target);
            }

            for (int i = 0; i < all.Length; i++)
            {
                Transform target = all[i];
                if (target != null && IsVisibleMemberRoot(target.name))
                    GroundTransformY(target);
            }
        }

        private void LateUpdate()
        {
            GroundNow();
        }

        private void GroundTransformY(Transform target)
        {
            Vector3 before = target.position;
            NavMeshHit hit;
            if (!NavMesh.SamplePosition(before, out hit, sampleDistance, NavMesh.AllAreas))
                return;
            if (Mathf.Abs(hit.position.y - referenceWorldY) > heightTolerance)
                return;

            Vector2 horizontalOffset = new Vector2(
                hit.position.x - before.x,
                hit.position.z - before.z);
            if (horizontalOffset.sqrMagnitude > 0.35f * 0.35f)
                return;

            if (Mathf.Abs(before.y - hit.position.y) <= 0.002f)
                return;

            before.y = hit.position.y;
            target.position = before;

            if (logFirstCorrection && !loggedCorrection)
            {
                loggedCorrection = true;
                Debug.Log("[PopulationGenerator] Grounded generated group to NavMesh Y="
                    + hit.position.y.ToString("F3") + ".", this);
            }
        }

        private static bool IsGeneratedGroupRoot(string objectName)
        {
            return objectName.StartsWith("GeneratedMovingSocialGroup")
                || objectName.StartsWith("GeneratedStaticSocialGroup")
                || objectName.StartsWith("GeneratedDynamicAttentionGroup");
        }

        private static bool IsVisibleMemberRoot(string objectName)
        {
            return objectName.StartsWith("MovingGroupMember_")
                || objectName.StartsWith("StaticGroupMember_")
                || objectName.StartsWith("DynamicAttentionMember_");
        }
    }
}

