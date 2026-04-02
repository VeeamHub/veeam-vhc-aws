using VhcMonitor.Core.Clients;

namespace VhcMonitor.Infrastructure;

public record ServerContext(
    string Name,
    string ServerType,
    IVbrClient? VbrClient = null,
    IVbawsClient? VbawsClient = null,
    EmClient? EmClient = null);
