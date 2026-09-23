using Dipping.Core.Models;

namespace Dipping.Core.Interfaces
{
    public interface IConfigurationService
    {
        AppSettings Current { get; }
        Task LoadAsync();
        Task SaveAsync();
        List<CameraConfig> GetEnabledCameras();
    }
}
