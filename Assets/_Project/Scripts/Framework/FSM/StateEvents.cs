using System;
using KeepAlive.Core;

namespace KeepAlive.Framework.FSM
{
    /// <summary>×´Ì¬ÇÐ»»³É¹¦</summary>
    public struct StateChangedEvent : IEvent
    {
        public Type FromState;
        public Type ToState;
    }

    /// <summary>×´Ì¬ÇÐ»»Ê§°Ü£¨OnEnter »ò OnExit Òì³££©</summary>
    public struct StateTransitionFailedEvent : IEvent
    {
        public Type State;
        public string ErrorMessage;
    }
}