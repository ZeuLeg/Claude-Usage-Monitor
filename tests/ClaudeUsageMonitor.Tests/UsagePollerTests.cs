using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using ClaudeUsageMonitor;
using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class UsagePollerTests
{
    // ── IsConnectivityError ───────────────────────────────────────────────────

    [Fact]
    public void IsConnectivityError_SocketException_ReturnsTrue()
    {
        var ex = new SocketException((int)SocketError.HostNotFound);
        Assert.True(UsagePoller.IsConnectivityError(ex));
    }

    [Fact]
    public void IsConnectivityError_HttpRequestException_WrappingSocket_ReturnsTrue()
    {
        var inner = new SocketException((int)SocketError.AccessDenied);
        var ex = new HttpRequestException("connection error", inner);
        Assert.True(UsagePoller.IsConnectivityError(ex));
    }

    [Fact]
    public void IsConnectivityError_HttpRequestException_NoStatusCode_ReturnsTrue()
    {
        // No status code = network-level failure (server never reached)
        var ex = new HttpRequestException("network error");
        Assert.True(UsagePoller.IsConnectivityError(ex));
    }

    [Fact]
    public void IsConnectivityError_HttpRequestException_WithStatusCode_ReturnsFalse()
    {
        // Status code present = server responded with an error (not a connectivity issue)
        var ex = new HttpRequestException("server error", null, HttpStatusCode.InternalServerError);
        Assert.False(UsagePoller.IsConnectivityError(ex));
    }

    [Fact]
    public void IsConnectivityError_JsonException_ReturnsFalse()
    {
        var ex = new JsonException("unexpected token");
        Assert.False(UsagePoller.IsConnectivityError(ex));
    }

    [Fact]
    public void IsConnectivityError_GenericException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("something went wrong");
        Assert.False(UsagePoller.IsConnectivityError(ex));
    }

    [Fact]
    public void IsConnectivityError_SocketException_DeepNested_ReturnsTrue()
    {
        var socket = new SocketException((int)SocketError.HostNotFound);
        var middle = new InvalidOperationException("wrapped", socket);
        var outer  = new HttpRequestException("outer", middle);
        Assert.True(UsagePoller.IsConnectivityError(outer));
    }
}
