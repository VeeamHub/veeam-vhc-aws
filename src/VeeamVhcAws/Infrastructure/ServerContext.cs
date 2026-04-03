using VeeamVhcAws.Core.Clients;

namespace VeeamVhcAws.Infrastructure;

public record ServerContext(
    string Name,
    string ServerType,
    IVbrClient? VbrClient = null,
    IVbawsClient? VbawsClient = null,
    EmClient? EmClient = null);
