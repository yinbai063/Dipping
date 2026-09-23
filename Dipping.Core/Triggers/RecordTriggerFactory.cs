using Dipping.Core.Interfaces;
using Dipping.Core.Models;
using Dipping.Core.Triggers;

namespace Dipping.Core.Triggers
{
    public static class RecordTriggerFactory
    {
        public static IRecordTrigger Create(TriggerConfig config, IAppLogger logger)
        {
            if (string.Equals(config.Source, TriggerConfig.SourcePlc, StringComparison.OrdinalIgnoreCase))
                return new PlcRecordTrigger(logger);

            return new TcpRecordTrigger(logger, config.TcpPort);
        }
    }
}
