using System;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Players;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.GameObjects
{
    /// <summary>
    ///     Represents a map grid inside the ECS system.
    /// </summary>
    public interface IMapGridComponent : IComponent
    {
        GridId GridIndex { get; }
        IMapGrid Grid { get; }
        void ClearGridId();

        bool AnchorEntity(ITransformComponent transform);
        void UnanchorEntity(ITransformComponent transform);
        void AnchoredEntityDirty(ITransformComponent transform);
    }

    /// <inheritdoc cref="IMapGridComponent"/>
    [ComponentReference(typeof(IMapGridComponent))]
    [NetworkedComponent()]
    internal class MapGridComponent : Component, IMapGridComponent
    {
        [Dependency] private readonly IMapManager _mapManager = default!;

        [ViewVariables(VVAccess.ReadOnly)]
        [DataField("index")]
        private GridId _gridIndex = GridId.Invalid;

        /// <inheritdoc />
        public override string Name => "MapGrid";

        /// <inheritdoc />
        public GridId GridIndex
        {
            get => _gridIndex;
            internal set => _gridIndex = value;
        }

        /// <inheritdoc />
        [ViewVariables]
        public IMapGrid Grid => _mapManager.GetGrid(_gridIndex);

        /// <inheritdoc />
        public void ClearGridId()
        {
            _gridIndex = GridId.Invalid;
        }

        protected override void Initialize()
        {
            base.Initialize();
            var mapId = Owner.Transform.MapID;

            if (_mapManager.HasMapEntity(mapId))
            {
                Owner.Transform.AttachParent(_mapManager.GetMapEntity(mapId));
            }
        }

        /// <inheritdoc />
        public bool AnchorEntity(ITransformComponent transform)
        {
            var xform = (TransformComponent) transform;
            var tileIndices = Grid.TileIndicesFor(transform.Coordinates);
            var result = Grid.AddToSnapGridCell(tileIndices, transform.Owner.Uid);

            if (result)
            {
                xform.Parent = Owner.Transform;

                // anchor snapping
                xform.LocalPosition = Grid.GridTileToLocal(Grid.TileIndicesFor(xform.WorldPosition)).Position;

                xform.SetAnchored(result);

                if (xform.Owner.TryGetComponent<PhysicsComponent>(out var physicsComponent))
                {
                    physicsComponent.BodyType = BodyType.Static;
                }
            }

            return result;
        }

        /// <inheritdoc />
        public void UnanchorEntity(ITransformComponent transform)
        {
            //HACK: Client grid pivot causes this.
            //TODO: make grid components the actual grid
            if(GridIndex == GridId.Invalid)
                return;

            var xform = (TransformComponent)transform;
            var tileIndices = Grid.TileIndicesFor(transform.Coordinates);
            Grid.RemoveFromSnapGridCell(tileIndices, transform.Owner.Uid);
            xform.SetAnchored(false);
            if (xform.Owner.TryGetComponent<PhysicsComponent>(out var physicsComponent))
            {
                physicsComponent.BodyType = BodyType.Dynamic;
            }
        }

        /// <inheritdoc />
        public void AnchoredEntityDirty(ITransformComponent transform)
        {
            if (!transform.Anchored)
                return;

            var grid = (IMapGridInternal) _mapManager.GetGrid(transform.GridID);
            grid.AnchoredEntDirty(grid.TileIndicesFor(transform.Coordinates));
        }

        /// <param name="player"></param>
        /// <inheritdoc />
        public override ComponentState GetComponentState(ICommonSession player)
        {
            return new MapGridComponentState(_gridIndex);
        }

        /// <inheritdoc />
        public override void HandleComponentState(ComponentState? curState, ComponentState? nextState)
        {
            base.HandleComponentState(curState, nextState);

            if (curState is not MapGridComponentState state)
                return;

            _gridIndex = state.GridIndex;
        }
    }

    /// <summary>
    ///     Serialized state of a <see cref="MapGridComponentState"/>.
    /// </summary>
    [Serializable, NetSerializable]
    internal class MapGridComponentState : ComponentState
    {
        /// <summary>
        ///     Index of the grid this component is linked to.
        /// </summary>
        public GridId GridIndex { get; }

        /// <summary>
        ///     Constructs a new instance of <see cref="MapGridComponentState"/>.
        /// </summary>
        /// <param name="gridIndex">Index of the grid this component is linked to.</param>
        public MapGridComponentState(GridId gridIndex)
        {
            GridIndex = gridIndex;
        }
    }
}
