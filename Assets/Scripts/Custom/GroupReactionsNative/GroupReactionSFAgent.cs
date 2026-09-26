using UnityEngine;

namespace SEAN.Scenario.Agents
{
    // Uses the supplied SFAgent force solver and PedestrianModulator. No second flee/follow solver.
    // The original group's Transform movement still owns the ordinary formation phase.
    public sealed class GroupReactionSFAgent : IVI.SFAgent
    {
        internal GroupMemberNativeReaction bridge;
        internal bool initialized;
        private PedestrianModulator mod;
        private CapsuleCollider socialCollider;

        protected override void Start()
        {
            // The bridge waits for the native scene robot and checks the mesh before enabling us.
            mod = GetComponent<PedestrianModulator>();
            var capsule = GetComponent<CapsuleCollider>();
            Vector3 center = capsule ? capsule.center : Vector3.zero;
            float radius = capsule ? capsule.radius : 0f;
            float height = capsule ? capsule.height : 0f;
            bool wasEnabled = capsule && capsule.enabled;
            bool wasTrigger = capsule && capsule.isTrigger;
            base.Start();
            // Base.Start computes a root capsule. Preserve the old group's body-collision settings.
            if (capsule) {
                capsule.center = center; capsule.radius = radius; capsule.height = height;
                capsule.enabled = wasEnabled; capsule.isTrigger = wasTrigger;
            } else if (collisionCapsule) collisionCapsule.enabled = false;
            socialCollider = gameObject.AddComponent<CapsuleCollider>();
            socialCollider.center = collisionCapsule.center;
            socialCollider.radius = collisionCapsule.radius;
            socialCollider.height = collisionCapsule.height;
            socialCollider.isTrigger = false;
            socialCollider.enabled = false;
            foreach (var own in GetComponents<Collider>())
                foreach (var visible in GetComponentsInChildren<Collider>(true))
                    if (visible.gameObject != gameObject) Physics.IgnoreCollision(own, visible, true);
            rb.isKinematic = true;
            rb.useGravity = false;
            // Native velocity moves the root during a reaction; the existing controller supplies posture.
            // Formation movement and gait configuration are untouched.
            DirectVelocityDrive = true;
            mod.rootMotionTranslationFrozen = true;
            InitDest(transform.position);
            SetSocialPresence(false);
            initialized = true;
        }

        protected override Vector3 UpdateVelocity()
        {
            if (!bridge || !bridge.UseNativeVelocity || (mod && mod.inspectorHold)) return Vector3.zero;
            return base.UpdateVelocity();
        }

        protected override Vector3 ModulateVelocity(Vector3 value)
        {
            if (!bridge || !initialized) return Vector3.zero;
            // The original formation owns recovery. Do not let flee/follow forces fight it.
            if (bridge.Returning) return Vector3.zero;
            if (bridge.NativeReaction) return base.ModulateVelocity(value);
            // Let native Surprised observe the exit edge, without moving the formation member.
            if (mod && mod.personality == PedestrianModulator.PersonalityType.Surprised && bridge.TargetOutsideRange)
                base.ModulateVelocity(Vector3.zero);
            return Vector3.zero;
        }

        internal void SetSocialPresence(bool present)
        {
            if (present) GO2Agent[gameObject] = this;
            else GO2Agent.Remove(gameObject);
            if (socialCollider) socialCollider.enabled = present;
        }

        protected override void OnDrawGizmosSelected()
        {
            if (initialized) base.OnDrawGizmosSelected();
        }
    }
}
