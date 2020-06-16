﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using MixedRealityExtension.Core.Physics;
using System;
using System.Collections.Generic;
using MixedRealityExtension.Util;
using UnityEngine;

namespace MixedRealityExtension.Core
{
	public class RigidBodyPhysicsBridgeInfo
	{
		public RigidBodyPhysicsBridgeInfo(Guid id, UnityEngine.Rigidbody rb, bool ownership)
		{
			Id = id;
			RigidBody = rb;
			Ownership = ownership;
			lastTimeKeyFramedUpdate = 0.0f;
			lastValidLinerVelocityOrPos.Set(0.0f, 0.0f, 0.0f);
			lastValidAngularVelocityorAng.Set(0.0f, 0.0f, 0.0f);

			IsKeyframed = false;
		}

		/// the rigid body identifier
		public Guid Id;

		/// Unity rigid body 
		public UnityEngine.Rigidbody RigidBody;

		/// these 3 fields are used to store the actual velocities ,
		/// IF this body is owned then here we store the transform that last time was sent
		public float lastTimeKeyFramedUpdate;
		public UnityEngine.Vector3 lastValidLinerVelocityOrPos;
		public UnityEngine.Vector3 lastValidAngularVelocityorAng;

		/// true if this rigid body is owned by this client
		public bool Ownership;
		/// if the body is moved by key-framing then this is true
		public bool IsKeyframed;

		// <todo> add owner of the body for faster access ?

		/// when transmitting the transforms we store if this body is sleeping
		public Patching.Types.MotionType sendMotionType;
	}

	/// <summary>
	/// the main class that is the bridge between the MRE Unity and the networked physics logic
	/// </summary>
	public class PhysicsBridge
	{

		private int _countOwnedTransforms = 0;
		private int _countStreamedTransforms = 0;

		private SortedList<Guid, RigidBodyPhysicsBridgeInfo> _rigidBodies = new SortedList<Guid, RigidBodyPhysicsBridgeInfo>();

		TimeSnapshotManager _snapshotManager = new TimeSnapshotManager();

		/// the prediction object
		IPrediction _predictor = new PredictionInterpolation();

		public PhysicsBridge()
		{
		}

		#region Rigid Body Management

		public void addRigidBody(Guid id, UnityEngine.Rigidbody rigidbody, bool ownership)
		{
			UnityEngine.Debug.Assert(!_rigidBodies.ContainsKey(id), "PhysicsBridge already has an entry for rigid body with specified ID.");

			_rigidBodies.Add(id, new RigidBodyPhysicsBridgeInfo(id, rigidbody, ownership));

			if (ownership)
			{
				rigidbody.isKinematic = false;
				_countOwnedTransforms++;
			}
			else
			{
				rigidbody.isKinematic = true;
				_countStreamedTransforms++;
			}
		}

		public void removeRigidBody(Guid id)
		{
			UnityEngine.Debug.Assert(_rigidBodies.ContainsKey(id), "PhysicsBridge don't have rigid body with specified ID.");

			var rb = _rigidBodies[id];

			if (rb.Ownership)
			{
				_countOwnedTransforms--;
			}
			else
			{
				_countStreamedTransforms--;
				//<todo> remove also from the last Jitter buffer for sleeping bodies (we should just set the motion type to dynamic)
				_snapshotManager.DelteBodyFromBuffer(id);
			}

			_rigidBodies.Remove(id);
		}

		public void setRigidBodyOwnership(Guid id, bool ownership)
		{
			if (!_rigidBodies.ContainsKey(id))
			{
				return;
			}

			UnityEngine.Debug.Assert(_rigidBodies.ContainsKey(id), "PhysicsBridge don't have rigid body with specified ID.");
			UnityEngine.Debug.Assert(_rigidBodies[id].Ownership != ownership, "Rigid body with specified ID is already registered with same ownership flag.");

			if (ownership)
			{
				_countOwnedTransforms++;
				_countStreamedTransforms--;
			}
			else
			{
				_countOwnedTransforms--;
				_countStreamedTransforms++;
			}

			//<todo> this could be done more efficiently
			_snapshotManager.DelteBodyFromBuffer(id);
			
			_rigidBodies[id].Ownership = ownership;
		}

		public void setKeyframed(Guid id, bool isKeyFramed)
		{
			if (_rigidBodies.ContainsKey(id))
			{
				var rb = _rigidBodies[id];
				if (rb.Ownership)
				{
					rb.IsKeyframed = isKeyFramed;
				}
			}
		}

		#endregion

		#region Transform Streaming

		/// <summary>
		/// Add transform snapshot from specified source.
		/// </summary>
		/// <param name="sourceId">Snapshot source identifier.</param>
		/// <param name="snapshot">List of transform at specified timestamp.</param>
		public void addSnapshot(Guid sourceId, Snapshot snapshot)
		{

			// <todo> jabenk now not all bodies will get an update here
			//        we should just copy exactly over 

			_snapshotManager.addSnapshot(sourceId, snapshot);
		}

		#endregion

		public void FixedUpdate(UnityEngine.Transform rootTransform)
		{
			// - physics rigid body management
			// -set transforms/velocities for key framed bodies

			// get all the prediction time infos in this struct
			PredictionTimeParameters timeInfo = new PredictionTimeParameters(UnityEngine.Time.fixedDeltaTime);

			// start the predictor
			_predictor.StartBodyPredicitonForNextFrame();

			int index = 0;
			MultiSourceCombinedSnapshot snapshot = _snapshotManager.GetNextSnapshot(timeInfo.DT);

			foreach (var rb in _rigidBodies.Values)
			{
				// if the body is owned then we only set the kinematic flag for the physics
				if (rb.Ownership)
				{
					if (rb.IsKeyframed)
					{
						rb.RigidBody.isKinematic = true;
					}
					else
					{
						rb.RigidBody.isKinematic = false;
					}
					continue;
				}

				// Find corresponding rigid body info.
				// since both are sorted list this should hit without index=0 at the beginning
				while (index < snapshot.RigidBodies.Count && rb.Id.CompareTo(snapshot.RigidBodies.Values[index].Id) > 0)
				{
					index++;
				}

				if (index < snapshot.RigidBodies.Count && rb.Id == snapshot.RigidBodies.Values[index].Id)
				{
					// todo: kick-in prediction if we are missing an update for this rigid body
					//if (!snapshot.RigidBodies.Values[index].HasUpdate)
					//{
					//	rb.RigidBody.isKinematic = false;
					//	continue;
					//}

					RigidBodyTransform transform = snapshot.RigidBodies.Values[index].Transform;
					float timeOfSnapshot = snapshot.RigidBodies.Values[index].LocalTime;

					// get the key framed stream, and compute implicit velocities
					UnityEngine.Vector3 keyFramedPos = rootTransform.TransformPoint(transform.Position);
					UnityEngine.Quaternion keyFramedOrientation = rootTransform.rotation * transform.Rotation;
					// if there is a really new update then also store the implicit velocity
					if (rb.lastTimeKeyFramedUpdate < timeOfSnapshot)
					{
						// <todo> for long running times this could be a problem 
						float invUpdateDT = 1.0f / (timeOfSnapshot - rb.lastTimeKeyFramedUpdate);
						rb.lastValidLinerVelocityOrPos = (keyFramedPos - rb.RigidBody.transform.position) * invUpdateDT;
						// transform to radians and take the angular velocity 
						UnityEngine.Vector3 eulerAngles = (
						      UnityEngine.Quaternion.Inverse(rb.RigidBody.transform.rotation)
						    * keyFramedOrientation).eulerAngles;
						UnityEngine.Vector3 radianAngles = UtilMethods.TransformEulerAnglesToRadians(eulerAngles);
						rb.lastValidAngularVelocityorAng = radianAngles * invUpdateDT;
//#if MRE_PHYSICS_DEBUG
						Debug.Log(" Remote body: " + rb.Id.ToString() + " got update lin vel:"
							+ rb.lastValidLinerVelocityOrPos + " ang vel:" + rb.lastValidAngularVelocityorAng
							//+ " DangE:" + eulerAngles + " DangR:" + radianAngles
							+ " time:" + timeOfSnapshot + " newp:" + keyFramedPos
							+ " newR:" + keyFramedOrientation
							+ " incUpdateDt:" + invUpdateDT
							+ " oldP:" + rb.RigidBody.transform.position
							+ " oldR:" + rb.RigidBody.transform.rotation
							+ " OriginalRot:" + transform.Rotation
							+ " keyF:" + rb.RigidBody.isKinematic
							+ " KF:" + rb.IsKeyframed);
//#endif
					}
					rb.lastTimeKeyFramedUpdate = timeOfSnapshot;
					rb.IsKeyframed = (snapshot.RigidBodies.Values[index].motionType == Patching.Types.MotionType.Keyframed);

					// code to disable prediction and to use just key framing 
					rb.RigidBody.isKinematic = true;
					rb.RigidBody.transform.position = keyFramedPos;
					rb.RigidBody.transform.rotation = keyFramedOrientation;
					rb.RigidBody.velocity.Set(0.0f, 0.0f, 0.0f);
					rb.RigidBody.angularVelocity.Set(0.0f, 0.0f, 0.0f);

					// call the predictor with this remotely owned body
					//_predictor.AddAndProcessRemoteBodyForPrediction(rb, transform,
					//	keyFramedPos, keyFramedOrientation, timeOfSnapshot, timeInfo);
				}
			}

			// call the predictor
			//_predictor.PredictAllRemoteBodiesWithOwnedBodies(ref _rigidBodies, timeInfo);

		}

		/// <summary>
		/// Generate rigid body transform snapshot for owned transforms with specified timestamp.
		/// </summary>
		/// <param name="time">Snapshot timestamp.</param>
		/// <param name="rootTransform">Root transform.</param>
		/// <returns>Generated snapshot.</returns>
		public Snapshot GenerateSnapshot(float time, UnityEngine.Transform rootTransform)
		{
			// collect transforms from owned rigid bodies
			// and generate update packet/snapshot

			// these constants define when a body is considered to be sleeping
			const float globalToleranceMultipier = 1.0F;
			const float maxSleepingSqrtLinearVelocity = 0.05F * globalToleranceMultipier;
			const float maxSleepingSqrtAngularVelocity = 0.05F * globalToleranceMultipier;
			const float maxSleepingSqrtPositionDiff = 0.02F * globalToleranceMultipier;
			const float maxSleepingSqrtAngularEulerDiff = 5.0F * globalToleranceMultipier;
			int numSleepingBodies = 0;
			int numOwnedBodies = 0;

			List<Snapshot.TransformInfo> transforms = new List<Snapshot.TransformInfo>(_rigidBodies.Count);

			foreach (var rb in _rigidBodies.Values)
			{
				if (!rb.Ownership)
				{
					continue;
				}

				RigidBodyTransform transform;
				{
					transform.Position = rootTransform.InverseTransformPoint(rb.RigidBody.transform.position);
					transform.Rotation = UnityEngine.Quaternion.Inverse(rootTransform.rotation) * rb.RigidBody.transform.rotation;
				}

				numOwnedBodies++;
				Patching.Types.MotionType mType = (rb.IsKeyframed) ? (Patching.Types.MotionType.Keyframed)
					: (Patching.Types.MotionType.Dynamic);

				UnityEngine.Vector3 posDiff = rb.lastValidLinerVelocityOrPos - transform.Position;
				UnityEngine.Vector3 rotDiff = rb.lastValidAngularVelocityorAng - transform.Rotation.eulerAngles;

				bool isBodySleeping =
					!rb.IsKeyframed && // if body is key framed and owned then we should just feed the jitter buffer
					( rb.RigidBody.velocity.sqrMagnitude < maxSleepingSqrtLinearVelocity
					  && rb.RigidBody.angularVelocity.sqrMagnitude < maxSleepingSqrtAngularVelocity
					  && posDiff.sqrMagnitude < maxSleepingSqrtPositionDiff
					  && rotDiff.sqrMagnitude < maxSleepingSqrtAngularEulerDiff);

				// test if this is sleeping, and when this was newly added then 
				if (rb.lastTimeKeyFramedUpdate > 0.001F && isBodySleeping && rb.sendMotionType == Patching.Types.MotionType.Sleeping)
				{
					// condition for velocity and positions are triggered and we already told the consumers to make body sleep, so just skip this update
					numSleepingBodies++;
					continue;
				}
				mType = (isBodySleeping) ? (Patching.Types.MotionType.Sleeping) : mType;

				// store the last update informations
				rb.sendMotionType = mType;
				rb.lastTimeKeyFramedUpdate = time;
				rb.lastValidLinerVelocityOrPos = transform.Position;
				rb.lastValidAngularVelocityorAng = transform.Rotation.eulerAngles;

				transforms.Add(new Snapshot.TransformInfo(rb.Id, transform, mType));
//#if MRE_PHYSICS_DEBUG
				Debug.Log(" SEND Remote body: " + rb.Id.ToString() + " OriginalRot:" + transform.Rotation
					+ " RigidBodyRot:" + rb.RigidBody.transform.rotation
					+ " lv:" + rb.RigidBody.velocity + " av:" + rb.RigidBody.angularVelocity
					+ " posDiff:" + posDiff + " rotDiff:" + rotDiff + " isKeyF:" + rb.IsKeyframed);
//#endif
			}

//#if MRE_PHYSICS_DEBUG
				Debug.Log(" Client:" + " Total number of sleeping bodies: " + numSleepingBodies + " total RBs" + _rigidBodies.Count
			     + " num owned " + numOwnedBodies + " num sent transforms " + transforms.Count);
//#endif
			var ret = new Snapshot(time, transforms);
			return ret;
		}

		public void LateUpdate()
		{
			// smooth transform update to hide artifacts for rendering
		}
	}
}
