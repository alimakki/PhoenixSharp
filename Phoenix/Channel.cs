﻿using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

using ParamsType = System.Collections.Generic.Dictionary<string, object>;

namespace Phoenix {

	public class Channel {

		#region nested types

		public enum State {
			Closed,
			Joining,
			Joined,
			Leaving,
			Errored, // errored channels are rejoined automatically
		}

		public struct Subscription {
			public string @event;
			public Action<Message> callback;
		}

		#endregion


		#region properties

		public State state = State.Closed;
		public readonly string topic;
		private readonly ParamsType @params;
		public readonly Socket socket;

		private Dictionary<string, List<Subscription>> bindings = new();
		private TimeSpan timeout;
		private bool joinedOnce = false;
		private readonly Push joinPush;
		private List<Push> pushBuffer = new();

		/** 
		 *	See the stateChangeRefs comment in Socket.cs
		 */
		// internal List<object> stateChangeRefs = new();

		private readonly Scheduler rejoinTimer;
		internal string joinRef {
			get {
				return joinPush.@ref;
			}
		}

		#endregion


		public Channel(string topic, ParamsType @params, Socket socket) {

			this.topic = topic;
			// TODO: possibly support lazy instantiation of payload (same as Phoenix js)
			this.@params = @params;
			this.socket = socket;

			timeout = socket.opts.timeout;
			joinPush = new Push(
				this,
				Message.OutBoundEvent.phx_join.ToString(),
				() => @params,
				timeout
			);

			rejoinTimer = new Scheduler(
					() => { if (socket.IsConnected()) Rejoin(); },
					socket.opts.rejoinAfter,
					socket.opts.delayedExecutor
			);

			socket.OnError += SocketOnError;
			socket.OnOpen += SocketOnOpen;

			joinPush.Receive(Message.Reply.Status.ok, message => {
				state = State.Joined;
				rejoinTimer.Reset();
				pushBuffer.ForEach(push => push.Send());
				pushBuffer.Clear();
			});

			joinPush.Receive(Message.Reply.Status.error, message => {
				state = State.Errored;
				if (socket.IsConnected()) {
					rejoinTimer.ScheduleTimeout();
				}
			});

			OnClose(message => {
				rejoinTimer.Reset();
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"close {topic}");
				}
				state = State.Closed;
				socket.OnError -= SocketOnError;
				socket.OnOpen -= SocketOnOpen;
			});

			OnError(message => {
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"error {topic}");
				}
				if (IsJoining()) {
					joinPush.Reset();
				}
				state = State.Errored;
				if (socket.IsConnected()) {
					rejoinTimer.ScheduleTimeout();
				}
			});

			joinPush.Receive(Message.Reply.Status.timeout, message => {
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"timeout {topic} ({joinRef})");
				}

				var leaveEvent = Message.OutBoundEvent.phx_leave.ToString();
				var leavePush = new Push(this, leaveEvent, null, timeout);
				leavePush.Send();

				state = State.Errored;
				joinPush.Reset();

				if (socket.IsConnected()) {
					rejoinTimer.ScheduleTimeout();
				}
			});

			// on phx_reply, also trigger a message for the push using replyEventName
			On(Message.InBoundEvent.phx_reply.ToString(), message => {
				Trigger(new Message(
						topic: topic,
						@event: ReplyEventName(message.@ref),
						payload: message.payload,
						@ref: message.@ref,
						joinRef: message.joinRef
				));
			});
		}


		public Push Join(TimeSpan? timeout = null) {
			if (joinedOnce) {
				throw new Exception("tried to join multiple times. 'join' can only be called a single time per channel instance");
			}

			this.timeout = timeout ?? this.timeout;
			joinedOnce = true;
			Rejoin();
			return joinPush;
		}

		public void OnClose(Action<Message> callback) {
			On(Message.InBoundEvent.phx_close, callback);
		}

		public void OnError(Action<Message> callback) {
			On(Message.InBoundEvent.phx_error, callback);
		}

		public void On(Message.InBoundEvent @event, Action<Message> callback) {
			On(@event.ToString(), callback);
		}

		public Subscription On(string anyEvent, Action<Message> callback) {
			var subscription = new Subscription() {
				@event = anyEvent,
				callback = callback
			};

			var subscriptions = bindings.GetValueOrDefault(anyEvent) ?? (bindings[anyEvent] = new());
			subscriptions.Add(subscription);

			socket.Log(LogLevel.Debug, "channel", $"subscription added: {anyEvent} {callback.Target} {callback.Method.Name}");

			return subscription;
		}

		public bool Off(Subscription subscription) {
			return bindings
					.GetValueOrDefault(subscription.@event)?
					.Remove(subscription) ?? false;
		}

		public bool Off(Enum eventEnum) {
			return Off(eventEnum.ToString());
		}

		public bool Off(string anyEvent) {
			return bindings.Remove(anyEvent);
		}

		internal bool CanPush() {
			return socket.IsConnected() && IsJoined();
		}

		public Push Push(string @event, Dictionary<string, object> payload = null, TimeSpan? timeout = null) {
			if (!joinedOnce) {
				throw new Exception($"tried to push '{@event}' to '{topic}' before joining. Use channel.join() before pushing events");
			}

			var pushEvent = new Push(this, @event, () => payload, timeout ?? this.timeout);
			if (CanPush()) {
				pushEvent.Send();
			} else {
				pushEvent.StartTimeout();
				pushBuffer.Add(pushEvent);
			}

			return pushEvent;
		}

		public Push Leave(TimeSpan? timeout = null) {
			rejoinTimer.Reset();
			joinPush.CancelTimeout();

			state = State.Leaving;

			Action onClose = () => {
				if (socket.HasLogger()) {
					socket.Log(LogLevel.Debug, "channel", $"leave {topic}");
				}

				// TODO: figure out the best way to trigger this
				Trigger(new Message(
					@event: Message.InBoundEvent.phx_close.ToString()
				));
			};

			var leavePush = new Push(this, Message.OutBoundEvent.phx_leave.ToString(), null, timeout ?? this.timeout);
			leavePush
					.Receive(Message.Reply.Status.ok, (_) => onClose())
					.Receive(Message.Reply.Status.timeout, (_) => onClose());
			leavePush.Send();

			if (!CanPush()) {
				leavePush.Trigger(Message.Reply.Status.ok);
			}
			return leavePush;
		}

		// overrideable message hook
		public virtual Dictionary<string, object> OnMessage(Message message) {
			return message.payload;
		}

		internal bool IsMember(Message message) {
			if (topic != message.topic) {
				return false;
			}

			if (message.joinRef != null && message.joinRef != joinRef) {
				if (socket.HasLogger()) {
					socket.opts.logger.Log(
							LogLevel.Info,
							"Channel",
							$"dropping outdated message for topic '{topic}' (joinRef {message.joinRef} does not match joinRef {joinRef})"
					);
				}
				return false;
			} else {
				return true;
			}
		}

		private void Rejoin(TimeSpan? timeout = null) {
			if (IsLeaving()) {
				return;
			}

			socket.LeaveOpenTopic(topic);
			state = State.Joining;
			joinPush.Resend(timeout ?? this.timeout);
		}

		internal void Trigger(Message message) {
			var handledPayload = OnMessage(message);
			if (message.payload != null && handledPayload == null) {
				throw new Exception($"channel onMessage callbacks must return payload, modified or unmodified");
			}

			var eventBindings = bindings.GetValueOrDefault(message.@event);

			socket.Log(LogLevel.Debug, "channel", $"channel '{topic}' triggering: {message.@event}");
			eventBindings?.ForEach(subscription => {
				socket.Log(LogLevel.Debug, "channel", $"channel '{topic}' triggering event '{message.@event}' to {subscription.callback.Method.Name}");
				subscription.callback(new Message(
						message.topic,
						message.@event,
						handledPayload,
						message.@ref,
						message.joinRef ?? joinRef
				));
			});
		}

		internal string ReplyEventName(string @ref) {
			return $"{Message.Reply.replyEventPrefix}{@ref}";
		}

		internal bool IsClosed() => state == State.Closed;
		internal bool IsErrored() => state == State.Errored;
		internal bool IsJoined() => state == State.Joined;
		internal bool IsJoining() => state == State.Joining;
		internal bool IsLeaving() => state == State.Leaving;

		#region Socket Events

		private void SocketOnError(string message) {
			rejoinTimer.Reset();
		}

		private void SocketOnOpen() {
			rejoinTimer.Reset();
			if (IsErrored()) {
				Rejoin();
			}
		}

		#endregion
	}
}
