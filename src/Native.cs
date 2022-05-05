using System.Runtime.InteropServices;
using static ZWOptical.ASISDK.ASICameraDll2;

namespace Astap.Lib
{
    internal static class Native
    {
        const string astap_lib = "astap_lib";

        [DllImport(astap_lib, CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
        public static extern int analyse_fits(
            string fits,
            double snr_min,
            int max_stars,
            out double medianHFD,
            out double medianFWHM,
            out double background
        );

        [DllImport(astap_lib, CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool find_asi_camera_by_index(int index, out int cameraId, out ASI_ERROR_CODE error);

        [DllImport(astap_lib, CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool find_asi_camera_by_name(string name, out int cameraId, out ASI_ERROR_CODE error);

        [DllImport(astap_lib, CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool connect_asi_camera(int cameraId, out ASI_ERROR_CODE error);

        [DllImport(astap_lib, CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool disconnect_asi_camera(int cameraId, out ASI_ERROR_CODE error);
    }
}