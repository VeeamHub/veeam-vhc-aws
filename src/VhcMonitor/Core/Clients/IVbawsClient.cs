namespace VhcMonitor.Core.Clients;

public interface IVbawsClient
{
    List<Dictionary<string, object>> GetSessions(DateTime from, DateTime to);
    Dictionary<string, object> GetSessionDetails(string sessionId);
    List<Dictionary<string, object>> GetSessionLogs(string sessionId);
    List<Dictionary<string, object>> GetHealthCheckSessions();
}
