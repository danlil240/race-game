using UnityEngine;

namespace FamilyRacing
{
    // Small secondary motions on the art trial only. The driving rig continues
    // to own the seat, head look, hands, steering and all simulation state.
    public sealed class AssetTrialRacerLife : MonoBehaviour
    {
        public Transform[] eyes;
        public Transform[] ears;
        public Transform tail;
        public Transform scarf;
        public float phase;
        private Quaternion[] earRest;
        private Quaternion tailRest, scarfRest;

        private void Awake()
        {
            earRest = new Quaternion[ears.Length];
            for (var i = 0; i < ears.Length; i++) earRest[i] = ears[i].localRotation;
            if (tail != null) tailRest = tail.localRotation;
            if (scarf != null) scarfRest = scarf.localRotation;
        }

        private void LateUpdate()
        {
            float t = Time.time + phase;
            float cycle = Mathf.Repeat(t, 4.7f);
            float blink = cycle < .16f ? Mathf.Max(.07f, Mathf.Abs(cycle - .08f) / .08f) : 1f;
            foreach (var eye in eyes) eye.localScale = new Vector3(1f, blink, 1f);
            for (var i = 0; i < ears.Length; i++)
                ears[i].localRotation = earRest[i] * Quaternion.Euler(2f * Mathf.Sin(t * 2.1f + i), 0, 0);
            if (tail != null) tail.localRotation = tailRest * Quaternion.Euler(0, 6f * Mathf.Sin(t * 2.4f), 0);
            if (scarf != null) scarf.localRotation = scarfRest * Quaternion.Euler(3f * Mathf.Sin(t * 5f), 5f * Mathf.Sin(t * 3f), 0);
        }
    }
}
