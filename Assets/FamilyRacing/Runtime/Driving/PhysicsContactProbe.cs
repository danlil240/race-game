using System.Collections.Generic;
using UnityEngine;

namespace FamilyRacing
{
    // Records chassis collision callbacks during a local-scene Simulate.
    // Records are collected inside OnCollisionEnter/OnCollisionStay and
    // only interpreted afterwards by the owning PhysicsVehicleBody —
    // callbacks never step the world recursively or touch race rules.
    // The buffer is preallocated; steady-state adds write structs into a
    // pooled list with no managed allocation.
    public sealed class PhysicsContactProbe : MonoBehaviour
    {
        public struct Record
        {
            public Collider other;
            public Vector3 normal;
            public float relativeSpeed;
            public bool isEnter;
        }

        public readonly List<Record> records = new List<Record>(32);

        public void ResetFrame()
        {
            records.Clear();
        }

        private void OnCollisionEnter(Collision collision)
        {
            Add(collision, true);
        }

        private void OnCollisionStay(Collision collision)
        {
            Add(collision, false);
        }

        private void Add(Collision collision, bool isEnter)
        {
            if (collision == null || collision.contactCount == 0) return;
            var contact = collision.GetContact(0); // struct: no allocation
            records.Add(new Record
            {
                other = collision.collider,
                normal = contact.normal,
                relativeSpeed = collision.relativeVelocity.magnitude,
                isEnter = isEnter
            });
        }
    }
}
