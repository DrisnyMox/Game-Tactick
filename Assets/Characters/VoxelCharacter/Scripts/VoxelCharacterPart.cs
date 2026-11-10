using UnityEngine;

namespace VoxelCharacterSystem
{
    /// <summary>
    /// Описание отдельной воксельной части персонажа (голова, торс, конечности, наплечники).
    /// Позволяет создавать модульную структуру, готовую к риггингу и анимации.
    /// </summary>
    public class VoxelCharacterPart
    {
        public string Name { get; set; }
        public VoxelModel Model { get; set; }
        public Vector3 LocalPosition { get; set; }
        public Quaternion LocalRotation { get; set; } = Quaternion.identity;
        public Vector3 PivotOffset { get; set; }

        public VoxelCharacterPart(string name, VoxelModel model, Vector3 localPosition, Vector3 pivotOffset)
        {
            Name = name;
            Model = model;
            LocalPosition = localPosition;
            PivotOffset = pivotOffset;
        }

        public Mesh GenerateMesh(float voxelSize, bool smoothNormals = true)
        {
            var mesh = VoxelMeshGenerator.GenerateMesh(Model, voxelSize, PivotOffset, smoothNormals);
            mesh.name = $"VoxelPart_{Name}";
            return mesh;
        }
    }
}
