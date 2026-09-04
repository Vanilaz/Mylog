using MyLog.Models;

namespace MyLog.Options;

public sealed class ClusterOptions
{
    public List<ClusterConfig> Clusters { get; set; } = [];
}
