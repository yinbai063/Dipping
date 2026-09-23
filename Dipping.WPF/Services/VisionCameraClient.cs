using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MvVSControlSDKNet;

namespace Dipping.WPF.Services
{
    /// <summary>
    /// 登录不改触发。Live 必须临时改成内部触发（TriggerMode=off1）才能连续出图；
    /// 停止预览 / 登出时写回外部触发（on1）以及原来的触发源（IO / 按钮 / 软件 / 通信）。
    /// </summary>
    public sealed class VisionCameraClient : IDisposable
    {
        public const string OccupiedLoginMessage = "相机正在被登录使用中，请先登出（勿与 NCM 采集同时登录同一台）。";

        private readonly CDevice _device = new();
        private bool _loggedIn;
        private bool _disposed;
        private bool _liveActive;
        private bool _burstActive;
        private uint _homeAcquisitionMode = 2;
        private uint _homeTriggerMode = 1;
        private bool _haveTriggerMode;
        private uint _homeTriggerSource;
        private bool _haveTriggerSource;
        private CStream? _resultStream;

        public string Ip { get; }

        private VisionCameraClient(string ip) => Ip = ip;

        public static bool TryConnect(
            string ip, string userName, string password, MV_VS_DEVICE_INFO stDev,
            out VisionCameraClient? client, out string error)
        {
            client = null;
            error = string.Empty;
            var loginUser = NormalizeUserName(userName);
            var loginPassword = password ?? string.Empty;
            if (string.IsNullOrEmpty(loginPassword))
            {
                error = "登录密码为空";
                return false;
            }

            var created = new VisionCameraClient(ip);
            var nRet = created._device.CreateHandle(ref stDev);
            if (nRet != CErrorCode.MV_VS_OK)
            {
                error = $"CreateHandle 失败: 0x{nRet:X}";
                return false;
            }

            nRet = created._device.Login(loginUser, loginPassword);
            if (nRet != CErrorCode.MV_VS_OK)
            {
                created._device.DestroyHandle();
                error = FormatLoginError(nRet, loginUser);
                return false;
            }

            created._loggedIn = true;
            created.CaptureHomeState();
            client = created;
            return true;
        }

        public static bool TryFindAndConnect(
            string ip, string userName, string password,
            out VisionCameraClient? client, out string error)
        {
            client = null;
            if (!TryFindDeviceByIp(ip, out var info, out error))
                return false;
            return TryConnect(ip, userName, password, info, out client, out error);
        }

        public static bool TryFindDeviceByIp(string ip, out MV_VS_DEVICE_INFO info, out string error)
        {
            info = default;
            error = string.Empty;
            VisionSdkGate.Process.Wait();
            try
            {
                var list = new MV_VS_DEVICE_INFO_LIST { nDeviceNum = 0 };
                var nRet = CSystem.EnumDevices(ref list);
                if (nRet != CErrorCode.MV_VS_OK)
                {
                    error = $"枚举设备失败: 0x{nRet:X}";
                    return false;
                }

                if (list.nDeviceNum == 0 || list.pDeviceInfo == null)
                {
                    error = "未枚举到任何相机";
                    return false;
                }

                var count = Math.Min(list.nDeviceNum, (uint)list.pDeviceInfo.Length);
                for (uint i = 0; i < count; i++)
                {
                    var ptr = list.pDeviceInfo[i];
                    if (ptr == IntPtr.Zero)
                        continue;
                    var item = Marshal.PtrToStructure<MV_VS_DEVICE_INFO>(ptr);
                    if (string.Equals(VisionCameraEnumService.FormatIp(item.nCurrentIp), ip, StringComparison.OrdinalIgnoreCase))
                    {
                        info = item;
                        return true;
                    }
                }

                error = $"子网内未找到 IP 为 {ip} 的相机";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                VisionSdkGate.Process.Release();
            }
        }

        public void StartLivePreview()
        {
            if (_liveActive)
                return;

            CaptureHomeState();
            try
            {
                ForceStopRun();
                Thread.Sleep(200);
                WriteTriggerMode(0);
                try { PrepareStreamParams(2); } catch { }
                var nRet = ResultStream.StartRun();
                if (nRet != CErrorCode.MV_VS_OK && !IsAlreadyRunningError(nRet))
                    throw new InvalidOperationException($"实时预览 StartRun 失败: 0x{nRet:X}");
                Thread.Sleep(120);
                WriteTriggerMode(0);
                Thread.Sleep(80);
                WriteTriggerMode(0);
                _liveActive = true;
            }
            catch
            {
                StopLivePreviewAndRestore();
                throw;
            }
        }

        public string? StopLivePreviewAndRestore()
        {
            var errors = new List<string>();
            try
            {
                ForceStopRun();
                Thread.Sleep(250);
                ForceStopRun();
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                errors.Add("StopRun：" + ex.Message);
            }

            RestoreExternalTrigger();
            ForceStopRun();
            _liveActive = false;
            _resultStream = null;
            return errors.Count == 0 ? null : string.Join("；", errors);
        }

        public VisionGrabResult? TryGetLiveFrame(int timeoutMs)
        {
            var stream = ResultStream;
            var frame = new MV_VS_DATA();
            // 与 NCM / 官方 Demo 一样：这是最长等待，有图立刻返回。
            // 过短（如 30ms）时大 JPEG 传不完会超时，只能隔很多次才拿到一帧，帧率会掉到 6 帧左右。
            var waitMs = timeoutMs > 0 ? (uint)timeoutMs : 200u;
            var nRet = stream.GetResultData(ref frame, waitMs);
            if (nRet != CErrorCode.MV_VS_OK)
                return null;

            byte[]? bytes = null;
            uint width = 0, height = 0;
            var pixelType = default(MvVSGvspPixelType);
            try
            {
                if (frame.pImageData == IntPtr.Zero || frame.nImageLen == 0)
                    return null;
                bytes = new byte[frame.nImageLen];
                Marshal.Copy(frame.pImageData, bytes, 0, (int)frame.nImageLen);
                width = frame.nImageWidth;
                height = frame.nImageHeight;
                pixelType = frame.enPixelType;
            }
            finally
            {
                stream.ReleaseResultData(ref frame);
            }

            if (bytes == null)
                return null;

            // Live 热路径：JPEG 先不解码，避免取流线程被 WPF 解码拖到 100～200ms。
            if (pixelType == MvVSGvspPixelType.MVVS_PixelType_Jpeg)
            {
                return new VisionGrabResult
                {
                    JpegBytes = bytes,
                    Width = width,
                    Height = height
                };
            }

            return new VisionGrabResult
            {
                Image = ToBitmapSource(bytes, width, height, pixelType),
                JpegBytes = bytes,
                Width = width,
                Height = height
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { StopLivePreviewAndRestore(); } catch { }
            _burstActive = false;
            try { if (_loggedIn) _device.Logout(); } catch { }
            try
            {
                if (_device.GetHandle() != IntPtr.Zero)
                    _device.DestroyHandle();
            }
            catch { }
        }

        public VisionGrabResult? GrabSoftwareTrigger(int timeoutMs, out string? error)
        {
            error = null;
            ForceStopRun();
            Thread.Sleep(200);
            try
            {
                StartRunWithMode(0);
                var firstWait = Math.Min(1000, timeoutMs);
                var grab = TryGetLiveFrame(firstWait);
                if (grab == null)
                {
                    ForceStopRun();
                    Thread.Sleep(150);
                    StartRunWithMode(0);
                    grab = TryGetLiveFrame(timeoutMs);
                }

                if (grab == null)
                    error = $"软件触发拍照超时：{timeoutMs}ms 内未收到图像。";
                return grab;
            }
            finally
            {
                ForceStopRun();
                Thread.Sleep(200);
                RestoreAcquisition();
            }
        }

        public void BeginBurstCapture()
        {
            ForceStopRun();
            Thread.Sleep(200);
            PrepareStreamParams(0);
            _burstActive = true;
        }

        public VisionGrabResult? GrabBurstFrame(int timeoutMs, out string? error)
        {
            error = null;
            if (!_burstActive)
            {
                error = "连拍未开始。";
                return null;
            }

            ForceStopRun();
            Thread.Sleep(60);
            StartRunWithMode(0);
            var grab = TryGetLiveFrame(Math.Min(1000, timeoutMs)) ?? TryGetLiveFrame(timeoutMs);
            if (grab == null)
                error = "连拍本张超时，未收到图像。";
            return grab;
        }

        public void EndBurstCapture()
        {
            _burstActive = false;
            ForceStopRun();
            Thread.Sleep(200);
            RestoreAcquisition();
        }

        private CStream ResultStream => _resultStream ??= new CStream(_device);

        private void CaptureHomeState()
        {
            _homeAcquisitionMode = 2;
            try
            {
                var ev = new MV_VS_ENUMVALUE();
                var nRet = new CParam(_device).GetEnumValue("AcquisitionMode", ref ev);
                if (nRet == CErrorCode.MV_VS_OK)
                    _homeAcquisitionMode = ev.nCurValue;
            }
            catch
            {
            }

            _haveTriggerMode = TryReadEnum("TriggerMode", out _homeTriggerMode);
            if (!_haveTriggerMode)
                _homeTriggerMode = 1;
            _haveTriggerSource = TryReadEnum("TriggerSource", out _homeTriggerSource);
        }

        private void RestoreAcquisition()
        {
            try
            {
                PrepareStreamParams(_homeAcquisitionMode);
                if (_homeAcquisitionMode == 2)
                    TryStartRun();
            }
            catch
            {
            }
        }

        /// <summary>停 Live 后写回登录时的触发。不再 StartRun，避免机身 Live 灯一直亮。</summary>
        private void RestoreExternalTrigger()
        {
            var mode = _haveTriggerMode ? _homeTriggerMode : 1u;
            WriteTriggerMode(mode);
            if (_haveTriggerSource)
                WriteTriggerSource(_homeTriggerSource);
            try { PrepareStreamParams(_homeAcquisitionMode); } catch { }
            Thread.Sleep(120);
            WriteTriggerMode(mode);
            if (_haveTriggerSource)
                WriteTriggerSource(_homeTriggerSource);
            Thread.Sleep(80);
            WriteTriggerMode(mode);
            if (_haveTriggerSource)
                WriteTriggerSource(_homeTriggerSource);
            ForceStopRun();
        }

        private static bool TryReadEnum(CDevice device, string name, out uint value)
        {
            value = 0;
            try
            {
                var ev = new MV_VS_ENUMVALUE();
                var nRet = new CParam(device).GetEnumValue(name, ref ev);
                if (nRet == CErrorCode.MV_VS_OK)
                {
                    value = ev.nCurValue;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool TryReadEnum(string name, out uint value) => TryReadEnum(_device, name, out value);

        private void StartRunWithMode(uint acquisitionMode)
        {
            PrepareStreamParams(acquisitionMode);
            var nRet = new CStream(_device).StartRun();
            if (nRet != CErrorCode.MV_VS_OK && !IsAlreadyRunningError(nRet))
                throw new InvalidOperationException($"StartRun 失败: 0x{nRet:X}");
        }

        private void WriteTriggerMode(uint mode)
        {
            var symbolic = mode == 0 ? "off1" : "on1";
            try { new CParam(_device).SetEnumValue("TriggerMode", mode); } catch { }
            try { new CParam(_device).SetEnumValueByString("TriggerMode", symbolic); } catch { }
        }

        private void WriteTriggerSource(uint source)
        {
            try { new CParam(_device).SetEnumValue("TriggerSource", source); } catch { }
            var symbolic = source switch
            {
                0 => "IOTrigger",
                10 => "software",
                12 => "communication",
                15 => "button",
                _ => null
            };
            if (symbolic != null)
            {
                try { new CParam(_device).SetEnumValueByString("TriggerSource", symbolic); } catch { }
            }
        }

        private void TryStartRun()
        {
            var nRet = new CStream(_device).StartRun();
            if (nRet == CErrorCode.MV_VS_OK || IsAlreadyRunningError(nRet))
                return;
        }

        private void ForceStopRun()
        {
            try { _resultStream?.StopRun(); } catch { }
            try { new CStream(_device).StopRun(); } catch { }
        }

        private void PrepareStreamParams(uint acquisitionMode)
        {
            var param = new CParam(_device);
            param.SetBoolValue("CommandImageMode", false);
            param.SetIntValue("ModuleID", 0);
            var nRet = param.SetEnumValue("AcquisitionMode", acquisitionMode);
            if (nRet != CErrorCode.MV_VS_OK)
                throw new InvalidOperationException($"Set AcquisitionMode={acquisitionMode} 失败: 0x{nRet:X}");
        }

        private static bool IsAlreadyRunningError(int nRet) =>
            nRet == CErrorCode.MV_VS_E_CALLORDER
            || nRet == CErrorCode.MV_VS_E_BUSY
            || nRet == CErrorCode.MV_VS_E_PRECONDITION
            || nRet == CErrorCode.MV_VS_E_GC_ACCESS;

        private static string FormatLoginError(int nRet, string loginUser)
        {
            if (nRet == CErrorCode.MV_VS_E_PASSWORD)
                return "登录密码错误";
            return OccupiedLoginMessage + $"（{loginUser}，0x{nRet:X}）";
        }

        public static string NormalizeUserName(string? userName)
        {
            if (string.Equals(userName, "管理员", StringComparison.Ordinal))
                return "Admin";
            return string.IsNullOrWhiteSpace(userName) ? "Admin" : userName.Trim();
        }

        public static bool IsOccupiedLogin(string? message) =>
            !string.IsNullOrWhiteSpace(message)
            && (message.Contains("正在被登录使用中", StringComparison.Ordinal)
                || message.Contains("占用", StringComparison.Ordinal)
                || message.Contains("CONNECT_EXIST", StringComparison.OrdinalIgnoreCase));

        private static BitmapSource ToBitmapSource(byte[] data, uint width, uint height, MvVSGvspPixelType pixelType)
        {
            if (pixelType == MvVSGvspPixelType.MVVS_PixelType_Jpeg)
                return VisionGrabResult.DecodeJpeg(data);

            if (width == 0 || height == 0)
                throw new InvalidOperationException($"图像宽高无效：{width}x{height}");

            BitmapSource bitmap;
            if (pixelType == MvVSGvspPixelType.MVVS_PixelType_Mono8)
            {
                bitmap = BitmapSource.Create(
                    (int)width, (int)height, 96, 96,
                    PixelFormats.Gray8, null, data, (int)width);
            }
            else if (pixelType == MvVSGvspPixelType.MVVS_PixelType_RGB8_Packed)
            {
                bitmap = BitmapSource.Create(
                    (int)width, (int)height, 96, 96,
                    PixelFormats.Rgb24, null, data, (int)width * 3);
            }
            else
            {
                throw new InvalidOperationException($"暂不支持的像素格式：{pixelType}");
            }

            bitmap.Freeze();
            return bitmap;
        }
    }

    public sealed class VisionGrabResult
    {
        public ImageSource? Image { get; set; }
        public byte[]? JpegBytes { get; init; }
        public uint Width { get; init; }
        public uint Height { get; init; }

        public BitmapSource? EnsureBitmap()
        {
            if (Image is BitmapSource existing)
                return existing;
            if (JpegBytes is not { Length: > 0 } jpeg)
                return null;
            var bmp = DecodeJpeg(jpeg);
            Image = bmp;
            return bmp;
        }

        public static BitmapSource DecodeJpeg(byte[] data)
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(data);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
    }
}
