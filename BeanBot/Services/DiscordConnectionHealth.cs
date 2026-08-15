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
        private DateTimeOffset? _unhealthySinceAtUtc;
        private string? _mostRecentDisconnectReason;

        public void MarkReady()
        {
            lock (_syncRoot)
            {
                _gatewayReady = true;
                _lastReadyAtUtc = DateTimeOffset.UtcNow;
                _unhealthySinceAtUtc = null;
            }
        }

        public void MarkDisconnected(Exception? exception)
        {
            lock (_syncRoot)
            {
                var disconnectedAtUtc = DateTimeOffset.UtcNow;
                _gatewayReady = false;
                _lastDisconnectedAtUtc = disconnectedAtUtc;
                _unhealthySinceAtUtc ??= disconnectedAtUtc;
                _mostRecentDisconnectReason = exception?.Message ?? "Discord gateway disconnected.";
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
                    _unhealthySinceAtUtc,
                    _mostRecentDisconnectReason);
            }
        }

        private string GetStatusMessage(LoginState loginState, ConnectionState connectionState)
        {
            if (_gatewayReady && loginState == LoginState.LoggedIn && connectionState == ConnectionState.Connected)
            {
                return "BeanBot is connected to Discord.";
            }

            if (_mostRecentDisconnectReason is not null)
            {
                return $"{_mostRecentDisconnectReason} Current state: login={loginState}, connection={connectionState}.";
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
            DateTimeOffset? unhealthySinceAtUtc,
            string? mostRecentDisconnectReason)
        {
            IsHealthy = isHealthy;
            StatusMessage = statusMessage;
            LoginState = loginState;
            ConnectionState = connectionState;
            LastReadyAtUtc = lastReadyAtUtc;
            LastDisconnectedAtUtc = lastDisconnectedAtUtc;
            UnhealthySinceAtUtc = unhealthySinceAtUtc;
            MostRecentDisconnectReason = mostRecentDisconnectReason;
        }

        public bool IsHealthy { get; }
        public string StatusMessage { get; }
        public string LoginState { get; }
        public string ConnectionState { get; }
        public DateTimeOffset? LastReadyAtUtc { get; }
        public DateTimeOffset? LastDisconnectedAtUtc { get; }
        public DateTimeOffset? UnhealthySinceAtUtc { get; }
        public string? MostRecentDisconnectReason { get; }
    }
}
