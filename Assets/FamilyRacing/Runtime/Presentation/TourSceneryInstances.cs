using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FamilyRacing
{
    // Reuse each imported tree/rock mesh instead of copying its vertices into
    // every road chunk. Visibility is controlled by the parent chunk.
    public sealed class TourSceneryInstances : MonoBehaviour
    {
        [Serializable] public sealed class Group
        {
            public Mesh mesh;
            public Material material;
            public int subMesh;
            public Matrix4x4[] localMatrices;
            [NonSerialized] public Matrix4x4[] worldMatrices;
        }
        public Group[] groups = Array.Empty<Group>();
        private Matrix4x4 cachedTransform;
        private bool cached;
        private void OnEnable() { cached=false; }
        private void LateUpdate() { Draw(); }
        public void Draw(Camera camera = null)
        {
            var matrix=transform.localToWorldMatrix;
            bool rebuild=!cached||matrix!=cachedTransform;
            foreach(var group in groups)
            {
                if(group.mesh==null||group.material==null||group.localMatrices==null||group.localMatrices.Length==0)continue;
                if(rebuild||group.worldMatrices==null)
                {
                    group.worldMatrices=new Matrix4x4[group.localMatrices.Length];
                    for(int i=0;i<group.worldMatrices.Length;i++)group.worldMatrices[i]=matrix*group.localMatrices[i];
                }
                if(SystemInfo.supportsInstancing)
                    Graphics.DrawMeshInstanced(group.mesh,group.subMesh,group.material,group.worldMatrices,
                        group.worldMatrices.Length,null,ShadowCastingMode.On,true,2,camera,LightProbeUsage.Off);
                else
                    foreach(var pose in group.worldMatrices)Graphics.DrawMesh(group.mesh,pose,group.material,2,camera,group.subMesh);
            }
            cachedTransform=matrix;cached=true;
        }
    }
}
