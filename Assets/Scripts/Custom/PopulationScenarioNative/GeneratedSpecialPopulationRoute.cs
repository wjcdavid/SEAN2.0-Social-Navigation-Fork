using System.Collections;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    // SpecialPopulationSpawner owns its world-root visual and removes it in OnDisable.
    // This host follows it for group obstacle references; it never drives the body itself.
    [DefaultExecutionOrder(950)]
    public sealed class GeneratedSpecialPopulationRoute : MonoBehaviour
    {
        private SpecialPopulationSpawner source;
        private bool addCollider, trigger;
        private float radius, height;
        public void Configure(SpecialPopulationSpawner value, bool add, bool isTrigger, float bodyRadius, float bodyHeight)
        {
            source = value; addCollider = add; trigger = isTrigger;
            radius = bodyRadius; height = bodyHeight;
        }
        IEnumerator Start()
        {
            float deadline = Time.realtimeSinceStartup + 15f;
            while (source && (!source.SpawnedCharacter || !source.SpawnedCharacter.activeInHierarchy)
                && !source.Status.StartsWith("ERROR:") && Time.realtimeSinceStartup < deadline) yield return null;
            if (!source || !source.SpawnedCharacter || !source.SpawnedCharacter.activeInHierarchy) {
                Debug.LogError("[Population native] Special character did not finish spawning: " + (source ? source.Status : "missing source"), this);
                yield break;
            }
            // Old group obstacle discovery ignores an empty transform. Replace the pending
            // host reference with the actual rendered character once asynchronous Build finishes.
            Transform populationRoot = transform.parent;
            if (populationRoot) {
                foreach (var group in populationRoot.GetComponentsInChildren<MovingSocialGroupSpawner_WJC_SlotFlip>(true)) {
                    group.explicitObstaclePeopleOrRoots.Remove(transform);
                    if (!group.explicitObstaclePeopleOrRoots.Contains(source.SpawnedCharacter.transform))
                        group.explicitObstaclePeopleOrRoots.Add(source.SpawnedCharacter.transform);
                }
                foreach (var group in populationRoot.GetComponentsInChildren<DynamicMovingAttentionGroupSpawner_WJC_SAFE_SlotFlip>(true)) {
                    group.explicitObstaclePeopleOrRoots.Remove(transform);
                    if (!group.explicitObstaclePeopleOrRoots.Contains(source.SpawnedCharacter.transform))
                        group.explicitObstaclePeopleOrRoots.Add(source.SpawnedCharacter.transform);
                }
            }
            if (addCollider) {
                var body = new GameObject("PopulationBodyCollider");
                body.transform.SetParent(source.SpawnedCharacter.transform, false);
                var capsule = body.AddComponent<CapsuleCollider>();
                capsule.radius = Mathf.Max(0.05f, radius); capsule.height = Mathf.Max(0.1f, height);
                capsule.center = Vector3.up * height * 0.5f; capsule.isTrigger = trigger;
            }
        }
        void LateUpdate()
        {
            if (source && source.SpawnedCharacter && source.SpawnedCharacter.activeInHierarchy)
                transform.position = source.SpawnedCharacter.transform.position;
        }
    }
}
