using System.Collections.Generic;
using UnityEngine;

namespace SEAN.Scenario.Agents
{
    // Keep disabled per-person reformation proxies out of the native force lookup.
    // Restore only registrations that really existed; don't pre-register before SFAgent.Start.
    public sealed class GroupProxyPresence : MonoBehaviour
    {
        private readonly Dictionary<GameObject, Base> removed = new Dictionary<GameObject, Base>();
        void OnDisable()
        {
            foreach (var agent in GetComponentsInChildren<IVI.SFAgent>(true)) {
                Base existing;
                if (IVI.SFAgent.GO2Agent.TryGetValue(agent.gameObject, out existing)) {
                    removed[agent.gameObject] = existing;
                    IVI.SFAgent.GO2Agent.Remove(agent.gameObject);
                }
            }
        }
        void OnEnable()
        {
            foreach (var entry in removed) if (entry.Key && entry.Value) IVI.SFAgent.GO2Agent[entry.Key] = entry.Value;
            removed.Clear();
        }
        void OnDestroy()
        {
            foreach (var agent in GetComponentsInChildren<IVI.SFAgent>(true)) IVI.SFAgent.GO2Agent.Remove(agent.gameObject);
        }
    }
}
