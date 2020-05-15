﻿using MixedRealityExtension.App;
using MixedRealityExtension.Behaviors.ActionData;
using MixedRealityExtension.Behaviors.Actions;
using MixedRealityExtension.Core.Interfaces;
using MixedRealityExtension.PluginInterfaces.Behaviors;
using System;

namespace MixedRealityExtension.Behaviors.Contexts
{
	public abstract class PhysicalToolBehaviorContext<ToolDataT> : BehaviorContextBase
		where ToolDataT : BaseToolData, new()
	{
		private ToolDataT _queuedToolData;
		private MWAction<ToolDataT> _holding = new MWAction<ToolDataT>();
		private MWAction<ToolDataT> _using = new MWAction<ToolDataT>();

		public void StartHolding(IUser user)
		{
			_holding.StartAction(user);
		}

		public void EndHolding(IUser user)
		{
			_holding.StopAction(user);
		}

		public void StartUsing(IUser user)
		{
			IsUsing = true;
			_using.StartAction(user);
		}

		public void EndUsing(IUser user)
		{
			IsUsing = false;
			_using.StopAction(user);
		}

		internal ToolDataT ToolData { get; private set; }

		internal bool IsUsing { get; private set; }


		internal PhysicalToolBehaviorContext()
			: base()
		{
			RegisterActionHandler(_holding, nameof(_holding));
			RegisterActionHandler(_using, nameof(_using));

			ToolData = new ToolDataT();
		}

		internal override sealed void SynchronizeBehavior()
		{
			if (IsUsing)
			{
				PerformUsingAction();
			}
		}

		private void OnUsingStateChanging(object sender, ActionStateChangedArgs args)
		{
			var wasUsing = IsUsing;
			IsUsing = args.NewState == ActionState.Started || args.NewState == ActionState.Performing;
			if (!IsUsing && wasUsing)
			{
				// We are stopping use and should send the remaining tool data up the remaining tool data from the last bit of use.
				PerformUsingAction();
			}
		}

		private void PerformUsingAction()
		{
			if (!ToolData.IsEmpty)
			{
				_queuedToolData = ToolData;
				ToolData = new ToolDataT();
				_using.PerformActionUpdate(_queuedToolData);
			}
		}
	}
}
