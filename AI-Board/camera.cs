using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AI_Board
{
    /// <summary>
    /// 原生 DirectShow 摄像头枚举器
    /// 保证提取的序号与 OpenCV (DSHOW 后端) 的索引 100% 一致
    /// </summary>
    public static class DirectShowCameraEnumerator
    {
        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICreateDevEnum
        {
            [PreserveSig]
            int CreateClassEnumerator(ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
        }
        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyBag
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string propName, out object pVar, int errorLog);
            [PreserveSig]
            int Write(string propName, ref object pVar);
        }

        public static List<string> GetCameras()
        {
            var cameras = new List<string>();
            object comObj = null;
            ICreateDevEnum enumDev = null;
            IEnumMoniker enumMon = null;

            try
            {
                // 获取系统设备枚举器的 COM 类型
                Type srvType = Type.GetTypeFromCLSID(new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"));
                if (srvType == null) return cameras;

                comObj = Activator.CreateInstance(srvType);
                enumDev = (ICreateDevEnum)comObj;

                // 重点：VideoInputDeviceCategory 视频输入设备分类（直接屏蔽掉打印机、扫描仪）
                Guid videoInputDeviceClass = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
                int hr = enumDev.CreateClassEnumerator(ref videoInputDeviceClass, out enumMon, 0);

                // hr != 0 说明没有设备
                if (hr != 0 || enumMon == null) return cameras;

                IMoniker[] monikers = new IMoniker[1];
                // 按照 DirectShow 的内部顺序遍历
                while (enumMon.Next(1, monikers, IntPtr.Zero) == 0 && monikers[0] != null)
                {
                    Guid bagId = typeof(IPropertyBag).GUID;
                    monikers[0].BindToStorage(null, null, ref bagId, out object bagObj);
                    IPropertyBag bag = (IPropertyBag)bagObj;

                    // 读取摄像头的友好名称 (FriendlyName)
                    hr = bag.Read("FriendlyName", out object val, 0);
                    if (hr == 0 && val != null)
                    {
                        cameras.Add((string)val);
                    }

                    if (bag != null) Marshal.ReleaseComObject(bag);
                    if (monikers[0] != null) Marshal.ReleaseComObject(monikers[0]);
                }
            }
            catch
            {
                // 忽略异常，返回当前已抓到的列表
            }
            finally
            {
                // 严格释放非托管内存
                if (enumMon != null) Marshal.ReleaseComObject(enumMon);
                if (enumDev != null) Marshal.ReleaseComObject(enumDev);
                if (comObj != null) Marshal.ReleaseComObject(comObj);
            }

            return cameras;
        }
    }
}