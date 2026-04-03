using System.Text.Json;
using VeeamVhcAws.Core.Models;
using VeeamVhcAws.Core.Output;

namespace VeeamVhcAws.Outputs;

public class JsonFileHandler : IOutputHandler
{
    private readonly string _path;
    private readonly bool _rotate;
    private readonly int _maxFiles;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public JsonFileHandler(string path = "/var/log/veeam-vhc-aws.json", bool rotate = false, int maxFiles = 30)
    {
        _path = path;
        _rotate = rotate;
        _maxFiles = maxFiles;
    }

    private void RotateFiles()
    {
        if (!File.Exists(_path))
            return;

        for (int i = _maxFiles - 1; i > 0; i--)
        {
            var src = $"{_path}.{i}";
            var dst = $"{_path}.{i + 1}";
            if (File.Exists(src))
            {
                if (i + 1 > _maxFiles)
                    File.Delete(src);
                else
                    File.Move(src, dst, overwrite: true);
            }
        }

        if (File.Exists(_path))
            File.Move(_path, $"{_path}.1", overwrite: true);
    }

    public void Emit(IReadOnlyList<MonitorResult> results)
    {
        if (_rotate)
            RotateFiles();

        var output = results.Select(r => r.ToDictionary()).ToList();

        var parent = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        var json = JsonSerializer.Serialize(output, Options);
        File.WriteAllText(_path, json + "\n");
    }
}
