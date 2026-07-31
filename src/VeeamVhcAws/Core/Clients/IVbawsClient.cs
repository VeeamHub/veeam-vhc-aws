namespace VeeamVhcAws.Core.Clients;

public interface IVbawsClient
{
    List<Dictionary<string, object>> GetSessions(DateTime from, DateTime to);
    Dictionary<string, object> GetSessionDetails(string sessionId);
    List<Dictionary<string, object>> GetSessionLogs(string sessionId);
    List<Dictionary<string, object>> GetHealthCheckSessions();

    // Issue #16: the global /sessions fetch is capped, hiding sessions for low-frequency policies.
    // These support per-policy session scoping to recover the complete picture.
    List<Dictionary<string, object>> GetPolicies();
    List<Dictionary<string, object>> GetSessionsForJob(string jobId, DateTime from, DateTime to);
}
