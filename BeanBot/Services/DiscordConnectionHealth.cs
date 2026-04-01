using Discord;
using Discord.WebSocket;

using System;

namespace BeanBot.Services
{
    public sealed class DiscordConnectionHealth
    {
        private readonly object _syncRoot = new();
        private bool _gatewayReady;
        private DateTimeOffset? _lastReadyAtUtc;
        private DateTimeOffset? _lastDisconnectedAtUtc;
        private string _lastDisconnectReason;

        public void MarkReady()
        {
            lock (_syncRoot)
            {
                _gatewayReady = true;
                _lastReadyAtUtc = DateTimeOffset.UtcNow;
                _lastDisconnectReason = null;
            }
        }

        public void MarkDisconnected(Exception exception)
        {
            lock (_syncRoot)
            {
                _gatewayReady = false;
                _lastDisconnectedAtUtc = DateTimeOffset.UtcNow;
                _lastDisconnectReason = exception?.Message ?? "Discord gateway disconnected.";
            }
        }

        public DiscordHealthSnapshot CreateSnapshot(DiscordSocketClient discordClient)
        {
            lock (_syncRoot)
            {
                var loginState = discordClient.LoginState;
                var connectionState = discordClient.ConnectionState;
                var isHealthy = loginState == LoginState.LoggedIn
                    && connectionState == ConnectionState.Connected
                    && _gatewayReady;

                return new DiscordHealthSnapshot(
                    isHealthy,
                    GetStatusMessage(loginState, connectionState),
                    loginState.ToString(),
                    connectionState.ToString(),
                    _lastReadyAtUtc,
                    _lastDisconnectedAtUtc,
                    _lastDisconnectReason);
            }
        }

        private string GetStatusMessage(LoginState loginState, ConnectionState connectionState)
        {
            if (_gatewayReady && loginState == LoginState.LoggedIn && connectionState == ConnectionState.Connected)
            {
                return "BeanBot is connected to Discord.";
            }

            if (_lastDisconnectReason is not null)
            {
                return $"{_lastDisconnectReason} Current state: login={loginState}, connection={connectionState}.";
            }

            if (!_gatewayReady)
            {
                return "Discord gateway has not reached the Ready state yet.";
            }

            if (loginState != LoginState.LoggedIn)
            {
                return $"Discord login state is {loginState}.";
            }

            return $"Discord connection state is {connectionState}.";
        }
    }

    public sealed class DiscordHealthSnapshot
    {
        public DiscordHealthSnapshot(
            bool isHealthy,
            string statusMessage,
            string loginState,
            string connectionState,
            DateTimeOffset? lastReadyAtUtc,
            DateTimeOffset? lastDisconnectedAtUtc,
            string lastDisconnectReason)
        {
            IsHealthy = isHealthy;
            StatusMessage = statusMessage;
            LoginState = loginState;
            ConnectionState = connectionState;
            LastReadyAtUtc = lastReadyAtUtc;
            LastDisconnectedAtUtc = lastDisconnectedAtUtc;
            LastDisconnectReason = lastDisconnectReason;
        }

        public bool IsHealthy { get; }
        public string StatusMessage { get; }
        public string LoginState { get; }
        public string ConnectionState { get; }
        public DateTimeOffset? LastReadyAtUtc { get; }
        public DateTimeOffset? LastDisconnectedAtUtc { get; }
        public string LastDisconnectReason { get; }
    }
}
