using System.Runtime.InteropServices;
namespace Astap.Lib;

public enum ASI_ERROR_CODE
{
    SUCCESS = 0,
    INVALID_INDEX, //no camera connected or index value out of boundary
    INVALID_ID, //invalid ID
    INVALID_CONTROL_TYPE, //invalid control type
    CAMERA_CLOSED, //camera didn't open
    CAMERA_REMOVED, //failed to find the camera, maybe the camera has been removed
    INVALID_PATH, //cannot find the path of the file
    INVALID_FILEFORMAT,
    INVALID_SIZE, //wrong video format size
    INVALID_IMGTYPE, //unsupported image formate
    OUTOF_BOUNDARY, //the startpos is out of boundary
    TIMEOUT, //timeout
    INVALID_SEQUENCE,//stop capture first
    BUFFER_TOO_SMALL, //buffer size is not big enough
    VIDEO_MODE_ACTIVE,
    EXPOSURE_IN_PROGRESS,
    GENERAL_ERROR,//general error, eg: value is out of valid range
    END
}

public static class AstapLib
{
    [DllImport("astap_lib", CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
    public static extern int analyse_fits(
        string fits,
        double snr_min,
        int max_stars,
        out double medianHFD,
        out double medianFWHM,
        out double background
    );

    [DllImport("astap_lib", CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool find_asi_camera_by_index(int index, out int cameraId, out ASI_ERROR_CODE error);

    [DllImport("astap_lib", CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool connect_asi_camera(int cameraId, out ASI_ERROR_CODE error);

    [DllImport("astap_lib", CharSet = CharSet.Unicode, SetLastError = false, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool disconnect_asi_camera(int cameraId, out ASI_ERROR_CODE error);
}