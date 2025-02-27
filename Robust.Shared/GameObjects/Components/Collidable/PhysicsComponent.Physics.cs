/*
* Farseer Physics Engine:
* Copyright (c) 2012 Ian Qvist
*
* Original source Box2D:
* Copyright (c) 2006-2011 Erin Catto http://www.box2d.org
*
* This software is provided 'as-is', without any express or implied
* warranty.  In no event will the authors be held liable for any damages
* arising from the use of this software.
* Permission is granted to anyone to use this software for any purpose,
* including commercial applications, and to alter it and redistribute it
* freely, subject to the following restrictions:
* 1. The origin of this software must not be misrepresented; you must not
* claim that you wrote the original software. If you use this software
* in a product, an acknowledgment in the product documentation would be
* appreciated but is not required.
* 2. Altered source versions must be plainly marked as such, and must not be
* misrepresented as being the original software.
* 3. This notice may not be removed or altered from any source distribution.
 *
 * PhysicsComponent is heavily modified from Box2D.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Broadphase;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Dynamics.Contacts;
using Robust.Shared.Players;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;

namespace Robust.Shared.GameObjects
{
    [ComponentReference(typeof(ILookupWorldBox2Component))]
    [ComponentReference(typeof(IPhysBody))]
    [NetworkedComponent()]
    public sealed class PhysicsComponent : Component, IPhysBody, ISerializationHooks, ILookupWorldBox2Component
    {
        [DataField("status", readOnly: true)]
        private BodyStatus _bodyStatus = BodyStatus.OnGround;

        /// <inheritdoc />
        public override string Name => "Physics";

        /// <summary>
        ///     Has this body been added to an island previously in this tick.
        /// </summary>
        public bool Island { get; set; }

        internal BroadphaseComponent? Broadphase { get; set; }

        /// <summary>
        ///     Store the body's index within the island so we can lookup its data.
        ///     Key is Island's ID and value is our index.
        /// </summary>
        public Dictionary<int, int> IslandIndex { get; set; } = new();

        // TODO: Actually implement after the initial pr dummy
        /// <summary>
        ///     Gets or sets where this body should be included in the CCD solver.
        /// </summary>
        public bool IsBullet { get; set; }

        public bool IgnoreCCD { get; set; }

        [ViewVariables]
        public int ContactCount
        {
            get
            {
                var count = 0;
                var edge = ContactEdges;
                while (edge != null)
                {
                    edge = edge.Next;
                    count++;
                }

                return count;
            }
        }

        [ViewVariables]
        public Box2 LocalAABB
        {
            get
            {
                var broadphaseSystem = EntitySystem.Get<SharedBroadphaseSystem>();
                var broadphase = broadphaseSystem.GetBroadphase(this);

                if (broadphase == null) return new Box2();

                var worldPos = Owner.Transform.WorldPosition;
                var aabb = new Box2(worldPos, worldPos);

                foreach (var fixture in Fixtures)
                {
                    foreach (var proxy in fixture.Proxies)
                    {
                        aabb = aabb.Union(proxy.AABB);
                    }
                }

                return aabb;
            }
        }

        [ViewVariables]
        public Box2 WorldAABB
        {
            get
            {
                var broadphaseSystem = EntitySystem.Get<SharedBroadphaseSystem>();
                var broadphase = broadphaseSystem.GetBroadphase(this);

                if (broadphase == null) return new Box2();
                var localAABB = LocalAABB;
                var center = broadphase.Owner.Transform.WorldMatrix.Transform(localAABB.Center);

                return new Box2Rotated(localAABB.Translated(center), broadphase.Owner.Transform.WorldRotation, center).CalcBoundingBox();
            }
        }

        /// <summary>
        ///     Linked-list of all of our contacts.
        /// </summary>
        internal ContactEdge? ContactEdges { get; set; } = null;

        public bool IgnorePaused { get; set; }

        internal SharedPhysicsMapComponent? PhysicsMap { get; set; }

        /// <inheritdoc />
        [ViewVariables(VVAccess.ReadWrite)]
        public BodyType BodyType
        {
            get => _bodyType;
            set
            {
                if (_bodyType == value)
                    return;

                var oldType = _bodyType;
                _bodyType = value;

                ResetMassData();

                if (_bodyType == BodyType.Static)
                {
                    SetAwake(false);
                    _linVelocity = Vector2.Zero;
                    _angVelocity = 0.0f;
                    // SynchronizeFixtures(); TODO: When CCD
                }
                else
                {
                    SetAwake(true);
                }

                Force = Vector2.Zero;
                Torque = 0.0f;

                EntitySystem.Get<SharedBroadphaseSystem>().RegenerateContacts(this);

                Owner.EntityManager.EventBus.RaiseLocalEvent(Owner.Uid, new PhysicsBodyTypeChangedEvent(_bodyType, oldType), false);
            }
        }


        [DataField("bodyType")]
        private BodyType _bodyType = BodyType.Static;

        /// <summary>
        /// Set awake without the sleeptimer being reset.
        /// </summary>
        internal void ForceAwake()
        {
            if (_awake || _bodyType == BodyType.Static) return;

            _awake = true;
            Owner.EntityManager.EventBus.RaiseEvent(EventSource.Local, new PhysicsWakeMessage(this));
        }

        // We'll also block Static bodies from ever being awake given they don't need to move.
        /// <inheritdoc />
        [ViewVariables(VVAccess.ReadWrite)]
        public bool Awake
        {
            get => _awake;
            set
            {
                if (_bodyType == BodyType.Static) return;

                SetAwake(value);
            }
        }

        private bool _awake = true;

        private void SetAwake(bool value)
        {
            if (_awake == value) return;
            _awake = value;

            if (value)
            {
                _sleepTime = 0.0f;
                Owner.EntityManager.EventBus.RaiseLocalEvent(Owner.Uid, new PhysicsWakeMessage(this));
            }
            else
            {
                Owner.EntityManager.EventBus.RaiseLocalEvent(Owner.Uid, new PhysicsSleepMessage(this));
                ResetDynamics();
                _sleepTime = 0.0f;
            }

            Dirty();
        }

        /// <summary>
        /// You can disable sleeping on this body. If you disable sleeping, the
        /// body will be woken.
        /// </summary>
        /// <value><c>true</c> if sleeping is allowed; otherwise, <c>false</c>.</value>
        [ViewVariables(VVAccess.ReadWrite)]
        public bool SleepingAllowed
        {
            get => _sleepingAllowed;
            set
            {
                if (_sleepingAllowed == value)
                    return;

                if (!value)
                    Awake = true;

                _sleepingAllowed = value;
                Dirty();
            }
        }

        [DataField("sleepingAllowed")]
        private bool _sleepingAllowed = true;

        [ViewVariables]
        public float SleepTime
        {
            get => _sleepTime;
            set
            {
                DebugTools.Assert(!float.IsNaN(value));

                if (MathHelper.CloseToPercent(value, _sleepTime))
                    return;

                _sleepTime = value;
            }
        }

        [DataField("sleepTime")]
        private float _sleepTime;

        /// <inheritdoc />
        public void WakeBody()
        {
            Awake = true;
        }

        void ISerializationHooks.AfterDeserialization()
        {
            FixtureCount = _fixtures.Count;

            foreach (var fixture in _fixtures)
            {
                fixture.Body = this;
                fixture.ComputeProperties();
                fixture.ID = GetFixtureName(fixture);
            }

            ResetMassData();

            if (_mass > 0f && (BodyType & (BodyType.Dynamic | BodyType.KinematicController)) != 0)
            {
                _invMass = 1.0f / _mass;
            }
        }

        /// <inheritdoc />
        public override ComponentState GetComponentState(ICommonSession session)
        {
            return new PhysicsComponentState(_canCollide, _sleepingAllowed, _fixedRotation, _bodyStatus, _fixtures, LinearVelocity, AngularVelocity, BodyType);
        }

        /// <inheritdoc />
        public override void HandleComponentState(ComponentState? curState, ComponentState? nextState)
        {
            if (curState is not PhysicsComponentState newState)
                return;

            SleepingAllowed = newState.SleepingAllowed;
            FixedRotation = newState.FixedRotation;
            CanCollide = newState.CanCollide;
            BodyStatus = newState.Status;

            // So transform doesn't apply MapId in the HandleComponentState because ??? so MapId can still be 0.
            // Fucking kill me, please. You have no idea deep the rabbit hole of shitcode goes to make this work.

            /*
             * -- Fixtures --
             */

            var toAddFixtures = new List<Fixture>();
            var toRemoveFixtures = new List<Fixture>();
            var computeProperties = false;

            // Given a bunch of data isn't serialized need to sort of re-initialise it
            var newFixtures = new List<Fixture>(newState.Fixtures.Count);
            foreach (var fixture in newState.Fixtures)
            {
                var newFixture = new Fixture();
                fixture.CopyTo(newFixture);
                newFixture.Body = this;
                newFixtures.Add(newFixture);
            }

            // Add / update new fixtures
            foreach (var fixture in newFixtures)
            {
                var found = false;

                foreach (var existing in _fixtures)
                {
                    if (!fixture.ID.Equals(existing.ID)) continue;

                    if (!fixture.Equals(existing))
                    {
                        toAddFixtures.Add(fixture);
                        toRemoveFixtures.Add(existing);
                    }

                    found = true;
                    break;
                }

                if (!found)
                {
                    toAddFixtures.Add(fixture);
                }
            }

            // Remove old fixtures
            foreach (var existing in _fixtures)
            {
                var found = false;

                foreach (var fixture in newFixtures)
                {
                    if (fixture.ID.Equals(existing.ID))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    toRemoveFixtures.Add(existing);
                }
            }

            var broadphaseSystem = EntitySystem.Get<SharedBroadphaseSystem>();

            foreach (var fixture in toRemoveFixtures)
            {
                computeProperties = true;
                broadphaseSystem.DestroyFixture(this, fixture);
            }

            // TODO: We also still need event listeners for shapes (Probably need C# events)
            foreach (var fixture in toAddFixtures)
            {
                computeProperties = true;
                broadphaseSystem.CreateFixture(this, fixture);
                fixture.Shape.ApplyState();
            }

            /*
             * -- Sundries --
             */

            Dirty();
            if (computeProperties)
            {
                ResetMassData();
            }

            LinearVelocity = newState.LinearVelocity;
            // Logger.Debug($"{IGameTiming.TickStampStatic}: [{Owner}] {LinearVelocity}");
            AngularVelocity = newState.AngularVelocity;
            BodyType = newState.BodyType;
            Predict = false;
        }

        public Fixture? GetFixture(string name)
        {
            // Sooo I'd rather have fixtures as a list in serialization but there's not really an easy way to have it as a
            // temporary value on deserialization so we can store it as a dictionary of <name, fixture>
            // given 100% of bodies have 1-2 fixtures this isn't really a performance problem right now but
            // should probably be done at some stage
            // If we really need it then you just deserialize onto a dummy field that then just never gets used again.
            foreach (var fixture in _fixtures)
            {
                if (fixture.ID.Equals(name))
                {
                    return fixture;
                }
            }

            return null;
        }

        /// <summary>
        /// Resets the dynamics of this body.
        /// Sets torque, force and linear/angular velocity to 0
        /// </summary>
        public void ResetDynamics()
        {
            Torque = 0;
            _angVelocity = 0;
            Force = Vector2.Zero;
            _linVelocity = Vector2.Zero;
            Dirty();
        }

        public Box2 GetWorldAABB(Vector2? worldPos = null, Angle? worldRot = null)
        {
            worldPos ??= Owner.Transform.WorldPosition;
            worldRot ??= Owner.Transform.WorldRotation;
            var transform = new Transform(worldPos.Value, (float) worldRot.Value.Theta);

            var bounds = new Box2(transform.Position, transform.Position);

            foreach (var fixture in _fixtures)
            {
                for (var i = 0; i < fixture.Shape.ChildCount; i++)
                {
                    var boundy = fixture.Shape.ComputeAABB(transform, i);
                    bounds = bounds.Union(boundy);
                }
            }

            return bounds;
        }

        [ViewVariables]
        public int FixtureCount { get; internal set; }

        /// <inheritdoc />
        [ViewVariables]
        public IReadOnlyList<Fixture> Fixtures => _fixtures;

        [DataField("fixtures")]
        [NeverPushInheritance]
        internal List<Fixture> _fixtures = new();

        /// <summary>
        ///     Enables or disabled collision processing of this component.
        /// </summary>
        /// <remarks>
        ///     Also known as Enabled in Box2D
        /// </remarks>
        [ViewVariables(VVAccess.ReadWrite)]
        public bool CanCollide
        {
            get => _canCollide;
            set
            {
                if (_canCollide == value ||
                    value && Owner.IsInContainer())
                    return;

                _canCollide = value;
                Owner.EntityManager.EventBus.RaiseEvent(EventSource.Local, new CollisionChangeMessage(this, Owner.Uid, _canCollide));
                Owner.EntityManager.EventBus.RaiseEvent(EventSource.Local, new PhysicsUpdateMessage(this));
                Dirty();
            }
        }

        [DataField("canCollide")]
        private bool _canCollide = true;

        /// <summary>
        ///     Non-hard physics bodies will not cause action collision (e.g. blocking of movement)
        ///     while still raising collision events. Recommended you use the fixture hard values directly
        /// </summary>
        /// <remarks>
        ///     This is useful for triggers or such to detect collision without actually causing a blockage.
        /// </remarks>
        [ViewVariables(VVAccess.ReadWrite)]
        public bool Hard
        {
            get
            {
                foreach (var fixture in Fixtures)
                {
                    if (fixture.Hard) return true;
                }

                return false;
            }
            set
            {
                foreach (var fixture in Fixtures)
                {
                    fixture.Hard = value;
                }
            }
        }

        /// <summary>
        ///     Bitmask of the collision layers this component is a part of.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public int CollisionLayer
        {
            get
            {
                var layers = 0x0;

                foreach (var fixture in Fixtures)
                    layers |= fixture.CollisionLayer;
                return layers;
            }
        }

        /// <summary>
        ///     Bitmask of the layers this component collides with.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public int CollisionMask
        {
            get
            {
                var mask = 0x0;

                foreach (var fixture in Fixtures)
                    mask |= fixture.CollisionMask;
                return mask;
            }
        }

        // I made Mass read-only just because overwriting it doesn't touch inertia.
        /// <summary>
        ///     Current mass of the entity in kilograms.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public float Mass => (BodyType & (BodyType.Dynamic | BodyType.KinematicController)) != 0 ? _mass : 0.0f;

        private float _mass;

        /// <summary>
        ///     Inverse mass of the entity in kilograms (1 / Mass).
        /// </summary>
        [ViewVariables]
        public float InvMass => (BodyType & (BodyType.Dynamic | BodyType.KinematicController)) != 0 ? _invMass : 0.0f;

        private float _invMass;

        /// <summary>
        /// Moment of inertia, or angular mass, in kg * m^2.
        /// </summary>
        /// <remarks>
        /// https://en.wikipedia.org/wiki/Moment_of_inertia
        /// </remarks>
        [ViewVariables(VVAccess.ReadWrite)]
        public float Inertia
        {
            get => _inertia + Mass * Vector2.Dot(LocalCenter, LocalCenter);
            set
            {
                DebugTools.Assert(!float.IsNaN(value));

                if (_bodyType != BodyType.Dynamic) return;

                if (MathHelper.CloseToPercent(_inertia, value)) return;

                if (value > 0.0f && !_fixedRotation)
                {
                    _inertia = value - Mass * Vector2.Dot(LocalCenter, LocalCenter);
                    DebugTools.Assert(_inertia > 0.0f);
                    InvI = 1.0f / _inertia;
                    Dirty();
                }
            }
        }

        private float _inertia;

        /// <summary>
        ///     Indicates whether this body ignores gravity
        /// </summary>
        public bool IgnoreGravity { get; set; }

        /// <summary>
        /// Inverse moment of inertia (1 / I).
        /// </summary>
        [ViewVariables]
        public float InvI { get; set; }

        /// <summary>
        ///     Is the body allowed to have angular velocity.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public bool FixedRotation
        {
            get => _fixedRotation;
            set
            {
                if (_fixedRotation == value)
                    return;

                _fixedRotation = value;
                _angVelocity = 0.0f;
                ResetMassData();
                Dirty();
            }
        }

        // TODO: Should default to false someday IMO
        [DataField("fixedRotation")]
        private bool _fixedRotation = true;

        // TODO: Will be used someday
        /// <summary>
        ///     Get this body's center of mass offset to world position.
        /// </summary>
        /// <remarks>
        ///     AKA Sweep.LocalCenter in Box2D.
        ///     Not currently in use as this is set after mass data gets set (when fixtures update).
        /// </remarks>
        public Vector2 LocalCenter
        {
            get => _localCenter;
            set
            {
                if (_bodyType != BodyType.Dynamic) return;
                if (value.EqualsApprox(_localCenter)) return;

                _localCenter = value;
            }
        }

        private Vector2 _localCenter = Vector2.Zero;

        /// <summary>
        /// Current Force being applied to this entity in Newtons.
        /// </summary>
        /// <remarks>
        /// The force is applied to the center of mass.
        /// https://en.wikipedia.org/wiki/Force
        /// </remarks>
        [ViewVariables(VVAccess.ReadWrite)]
        public Vector2 Force { get; set; }

        /// <summary>
        /// Current torque being applied to this entity in N*m.
        /// </summary>
        /// <remarks>
        /// The torque rotates around the Z axis on the object.
        /// https://en.wikipedia.org/wiki/Torque
        /// </remarks>
        [ViewVariables(VVAccess.ReadWrite)]
        public float Torque { get; set; }

        /// <summary>
        ///     Contact friction between 2 bodies.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public float Friction
        {
            get => _friction;
            set
            {
                if (MathHelper.CloseToPercent(value, _friction))
                    return;

                _friction = value;
                // TODO
                // Dirty();
            }
        }

        private float _friction;

        /// <summary>
        ///     This is a set amount that the body's linear velocity is reduced by every tick.
        ///     Combined with the tile friction.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public float LinearDamping
        {
            get => _linearDamping;
            set
            {
                DebugTools.Assert(!float.IsNaN(value));

                if (MathHelper.CloseToPercent(value, _linearDamping))
                    return;

                _linearDamping = value;
                // Dirty();
            }
        }

        [DataField("linearDamping")]
        private float _linearDamping = 0.2f;

        /// <summary>
        ///     This is a set amount that the body's angular velocity is reduced every tick.
        ///     Combined with the tile friction.
        /// </summary>
        /// <returns></returns>
        [ViewVariables(VVAccess.ReadWrite)]
        public float AngularDamping
        {
            get => _angularDamping;
            set
            {
                DebugTools.Assert(!float.IsNaN(value));

                if (MathHelper.CloseToPercent(value, _angularDamping))
                    return;

                _angularDamping = value;
                // Dirty();
            }
        }

        [DataField("angularDamping")]
        private float _angularDamping = 0.2f;

        /// <summary>
        /// Get the linear and angular velocities at the same time.
        /// </summary>
        public (Vector2 Linear, float Angular) MapVelocities
        {
            get
            {
                var linearVelocity = _linVelocity;
                var angularVelocity = _angVelocity;
                var parent = Owner.Transform.Parent?.Owner;

                while (parent != null)
                {
                    if (parent.TryGetComponent(out PhysicsComponent? body))
                    {
                        linearVelocity += body.LinearVelocity;
                        angularVelocity += body.AngularVelocity;
                    }

                    parent = parent.Transform.Parent?.Owner;
                }

                return (linearVelocity, angularVelocity);
            }
        }

        /// <summary>
        ///     Current linear velocity of the entity in meters per second.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public Vector2 LinearVelocity
        {
            get => _linVelocity;
            set
            {
                // Curse you Q
                // DebugTools.Assert(!float.IsNaN(value.X) && !float.IsNaN(value.Y));

                if (BodyType == BodyType.Static)
                    return;

                if (Vector2.Dot(value, value) > 0.0f)
                    Awake = true;

                if (_linVelocity.EqualsApprox(value, 0.0001f))
                    return;

                _linVelocity = value;
                Dirty();
            }
        }

        private Vector2 _linVelocity;

        /// <summary>
        /// Get the body's LinearVelocity in map terms.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="MapVelocities"/> if you need linear and angular at the same time.
        /// </remarks>
        public Vector2 MapLinearVelocity
        {
            get
            {
                var velocity = _linVelocity;
                var parent = Owner.Transform.Parent?.Owner;

                while (parent != null)
                {
                    if (parent.TryGetComponent(out PhysicsComponent? body))
                    {
                        velocity += body.LinearVelocity;
                    }

                    parent = parent.Transform.Parent?.Owner;
                }

                return velocity;
            }
        }

        /// <summary>
        ///     Current angular velocity of the entity in radians per sec.
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public float AngularVelocity
        {
            get => _angVelocity;
            set
            {
                // TODO: This and linearvelocity asserts
                // DebugTools.Assert(!float.IsNaN(value));

                if (BodyType == BodyType.Static)
                    return;

                if (value * value > 0.0f)
                    Awake = true;

                if (MathHelper.CloseToPercent(_angVelocity, value, 0.0001f))
                    return;

                _angVelocity = value;
                Dirty();
            }
        }

        private float _angVelocity;

        /// <summary>
        /// Get the body's AngularVelocity in map terms.
        /// </summary>
        /// <remarks>
        /// Consider using <see cref="MapVelocities"/> if you need linear and angular at the same time.
        /// </remarks>
        public float MapAngularVelocity
        {
            get
            {
                var velocity = _angVelocity;
                var parent = Owner.Transform.Parent?.Owner;

                while (parent != null)
                {
                    if (parent.TryGetComponent(out PhysicsComponent? body))
                    {
                        velocity += body.AngularVelocity;
                    }

                    parent = parent.Transform.Parent?.Owner;
                }

                return velocity;
            }
        }

        /// <summary>
        ///     Current momentum of the entity in kilogram meters per second
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public Vector2 Momentum
        {
            get => LinearVelocity * Mass;
            set => LinearVelocity = value / Mass;
        }

        /// <summary>
        ///     The current status of the object
        /// </summary>
        [ViewVariables(VVAccess.ReadWrite)]
        public BodyStatus BodyStatus
        {
            get => _bodyStatus;
            set
            {
                if (_bodyStatus == value)
                    return;

                _bodyStatus = value;
                Dirty();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public bool Predict
        {
            get => _predict;
            set => _predict = value;
        }

        private bool _predict;

        public IEnumerable<PhysicsComponent> GetBodiesIntersecting()
        {
            foreach (var entity in EntitySystem.Get<SharedPhysicsSystem>().GetCollidingEntities(Owner.Transform.MapID, GetWorldAABB()))
            {
                yield return entity;
            }
        }

        /// <summary>
        /// Gets a local point relative to the body's origin given a world point.
        /// Note that the vector only takes the rotation into account, not the position.
        /// </summary>
        /// <param name="worldPoint">A point in world coordinates.</param>
        /// <returns>The corresponding local point relative to the body's origin.</returns>
        public Vector2 GetLocalPoint(in Vector2 worldPoint)
        {
            return Transform.MulT(GetTransform(), worldPoint);
        }

        /// <summary>
        /// Get the world coordinates of a point given the local coordinates.
        /// </summary>
        /// <param name="localPoint">A point on the body measured relative the the body's origin.</param>
        /// <returns>The same point expressed in world coordinates.</returns>
        public Vector2 GetWorldPoint(in Vector2 localPoint)
        {
            return Transform.Mul(GetTransform(), localPoint);
        }

        public void FixtureChanged(Fixture fixture)
        {
            // TODO: Optimise this a LOT
            Dirty();
            Owner.EntityManager.EventBus.RaiseEvent(EventSource.Local, new FixtureUpdateMessage(this, fixture));
        }

        public string GetFixtureName(Fixture fixture)
        {
            if (!string.IsNullOrEmpty(fixture.ID)) return fixture.ID;

            var i = 0;

            while (true)
            {
                var found = false;
                ++i;
                var name = $"fixture_{i}";

                foreach (var existing in _fixtures)
                {
                    if (existing.ID.Equals(name))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return name;
                }
            }
        }

        internal Transform GetTransform()
        {
            return new(Owner.Transform.WorldPosition, (float) Owner.Transform.WorldRotation.Theta);
        }

        /// <summary>
        /// Applies an impulse to the centre of mass.
        /// </summary>
        public void ApplyLinearImpulse(in Vector2 impulse)
        {
            if ((_bodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0x0) return;
            Awake = true;

            LinearVelocity += impulse * _invMass;
        }

        /// <summary>
        /// Applies an impulse from the specified point.
        /// </summary>
        public void ApplyLinearImpulse(in Vector2 impulse, in Vector2 point)
        {
            if ((_bodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0x0) return;
            Awake = true;

            LinearVelocity += impulse * _invMass;
            // TODO: Sweep here
            AngularVelocity += InvI * Vector2.Cross(point, impulse);
        }

        public void ApplyAngularImpulse(float impulse)
        {
            if ((_bodyType & (BodyType.Dynamic | BodyType.KinematicController)) == 0x0) return;
            Awake = true;

            AngularVelocity += impulse * InvI;
        }

        public void ApplyForce(in Vector2 force)
        {
            if (_bodyType != BodyType.Dynamic) return;

            Awake = true;
            Force += force;
        }

        // TOOD: Need SetTransformIgnoreContacts so we can teleport body and /ignore contacts/
        public void DestroyContacts()
        {
            ContactEdge? contactEdge = ContactEdges;
            while (contactEdge != null)
            {
                var contactEdge0 = contactEdge;
                contactEdge = contactEdge.Next;
                PhysicsMap?.ContactManager.Destroy(contactEdge0.Contact!);
            }

            ContactEdges = null;
        }

        IEnumerable<IPhysBody> IPhysBody.GetCollidingEntities(Vector2 offset, bool approx)
        {
            return EntitySystem.Get<SharedPhysicsSystem>().GetCollidingEntities(this, offset, approx);
        }

        /// <inheritdoc />
        protected override void Initialize()
        {
            base.Initialize();

            if (BodyType == BodyType.Static)
            {
                _awake = false;
            }

            // TODO: Ordering fuckery need a new PR to fix some of this stuff
            if (Owner.Transform.MapID != MapId.Nullspace)
                PhysicsMap = IoCManager.Resolve<IMapManager>().GetMapEntity(Owner.Transform.MapID).GetComponent<SharedPhysicsMapComponent>();

            Dirty();
            // Yeah yeah TODO Combine these
            // Implicitly assume that stuff doesn't cover if a non-collidable is initialized.

            if (CanCollide)
            {
                if (!Awake)
                {
                    Owner.EntityManager.EventBus.RaiseEvent(EventSource.Local, new PhysicsSleepMessage(this));
                }
                else
                {
                    Owner.EntityManager.EventBus.RaiseEvent(EventSource.Local, new PhysicsWakeMessage(this));
                }

                if (Owner.IsInContainer())
                {
                    _canCollide = false;
                }
                else
                {
                    // TODO: Probably a bad idea but ehh future sloth's problem; namely that we have to duplicate code between here and CanCollide.
                    Owner.EntityManager.EventBus.RaiseLocalEvent(Owner.Uid, new CollisionChangeMessage(this, Owner.Uid, _canCollide));
                    Owner.EntityManager.EventBus.RaiseLocalEvent(Owner.Uid, new PhysicsUpdateMessage(this));
                }
            }
            else
            {
                _awake = false;
            }
        }

        protected override void OnRemove()
        {
            base.OnRemove();
            // Need to do these immediately in case collision behaviors deleted the body
            // TODO: Could be more optimal as currently broadphase will call this ANYWAY
            DestroyContacts();
            EntitySystem.Get<SharedBroadphaseSystem>().RemoveBody(this);
            CanCollide = false;
        }

        public void ResetMassData()
        {
            _mass = 0.0f;
            _invMass = 0.0f;
            _inertia = 0.0f;
            InvI = 0.0f;
            LocalCenter = Vector2.Zero;
            // Sweep

            if (((int) _bodyType & (int) BodyType.Kinematic) != 0)
            {
                return;
            }

            var localCenter = Vector2.Zero;

            foreach (var fixture in _fixtures)
            {
                if (fixture.Mass <= 0.0f) continue;

                var fixMass = fixture.Mass;

                _mass += fixMass;

                var center = Vector2.Zero;

                // TODO: God this is garbage
                switch (fixture.Shape)
                {
                    case PhysShapeAabb aabb:
                        center = aabb.Centroid;
                        break;
                    case PolygonShape poly:
                        center = poly.Centroid;
                        break;
                    case PhysShapeCircle circle:
                        center = circle.Position;
                        break;
                }

                localCenter += center * fixMass;
                _inertia += fixture.Inertia;
            }

            if (BodyType == BodyType.Static)
            {
                return;
            }

            if (_mass > 0.0f)
            {
                _invMass = 1.0f / _mass;
                localCenter *= _invMass;
            }
            else
            {
                // Always need positive mass.
                _mass = 1.0f;
                _invMass = 1.0f;
            }

            if (_inertia > 0.0f && !_fixedRotation)
            {
                // Center inertia about center of mass.
                _inertia -= _mass * Vector2.Dot(Vector2.Zero, Vector2.Zero);

                DebugTools.Assert(_inertia > 0.0f);
                InvI = 1.0f / _inertia;
            }
            else
            {
                _inertia = 0.0f;
                InvI = 0.0f;
            }

            LocalCenter = Vector2.Zero;
            /*
            var oldCenter = _sweep.Center;
            _sweep.LocalCenter = localCenter;
            _sweep.Center0 = _sweep.Center = Physics.Transform.Mul(GetTransform(), _sweep.LocalCenter);

            // Update center of mass velocity.
            var a = _sweep.Center - oldCenter;
            _linVelocity += new Vector2(-_angVelocity * a.y, _angVelocity * a.x);
            */
        }

        /// <summary>
        ///     Used to prevent bodies from colliding; may lie depending on joints.
        /// </summary>
        /// <param name="other"></param>
        /// <returns></returns>
        internal bool ShouldCollide(PhysicsComponent other)
        {
            if ((_bodyType & (BodyType.Kinematic | BodyType.Static)) != 0 &&
                (other._bodyType & (BodyType.Kinematic | BodyType.Static)) != 0)
            {
                return false;
            }

            // Does a joint prevent collision?
            // if one of them doesn't have jointcomp then they can't share a common joint.
            // otherwise, only need to iterate over the joints of one component as they both store the same joint.
            if (Owner.TryGetComponent(out JointComponent? jointComponentA) &&
                other.Owner.TryGetComponent(out JointComponent? jointComponentB))
            {
                var aUid = jointComponentA.Owner.Uid;
                var bUid = jointComponentB.Owner.Uid;

                ValueTuple<EntityUid, EntityUid> uids;

                uids = aUid.CompareTo(bUid) < 0 ?
                    new ValueTuple<EntityUid, EntityUid>(aUid, bUid) :
                    new ValueTuple<EntityUid, EntityUid>(bUid, aUid);

                foreach (var (_, joint) in jointComponentA.Joints)
                {
                    // Check if either: the joint even allows collisions OR the other body on the joint is actually the other body we're checking.
                    if (joint.CollideConnected ||
                        uids.Item1 != joint.BodyAUid ||
                        uids.Item2 != joint.BodyBUid) continue;

                    return false;
                }
            }

            var preventCollideMessage = new PreventCollideEvent(this, other);
            Owner.EntityManager.EventBus.RaiseLocalEvent(Owner.Uid, preventCollideMessage);

            if (preventCollideMessage.Cancelled) return false;

            preventCollideMessage = new PreventCollideEvent(other, this);
            Owner.EntityManager.EventBus.RaiseLocalEvent(other.Owner.Uid, preventCollideMessage);

            if (preventCollideMessage.Cancelled) return false;

            return true;
        }
    }

    /// <summary>
    ///     Directed event raised when an entity's physics BodyType changes.
    /// </summary>
    public class PhysicsBodyTypeChangedEvent : EntityEventArgs
    {
        /// <summary>
        ///     New BodyType of the entity.
        /// </summary>
        public BodyType New { get; }

        /// <summary>
        ///     Old BodyType of the entity.
        /// </summary>
        public BodyType Old { get; }

        public PhysicsBodyTypeChangedEvent(BodyType newType, BodyType oldType)
        {
            New = newType;
            Old = oldType;
        }
    }
}
