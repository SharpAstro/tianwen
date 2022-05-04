unit zwo;

{$mode ObjFPC}{$H+}
{$scopedEnums on}
{$Z4}

interface

  type
    ASI_INT = Int32;
{$ifdef CPU32}
    ASI_LONG = Int32;
{$endif}
{$ifdef CPU64}
  {$ifdef MSWINDOWS}
    ASI_LONG = Int32;
  {$else}
    ASI_LONG = Int64;
  {$endif}
{$endif}
    ASI_CAM_ID = type ASI_INT;
    ASI_ERROR = (success, invalid_index, invalid_id, invalid_control_type,
                 camera_closed, camera_removed, invalid_path, invalid_fileformat,
                 invalid_size, invalid_imgtype, outof_boundary, timeout,
                 invalid_sequence, buffer_too_small, video_mode_active,
                 exposure_in_process, general_error, invalid_mode);

  function find_asi_camera_by_index(const cameraIndex: ASI_INT; out cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;

  function find_asi_camera_by_name(const name: PWideChar; out cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;

  function connect_asi_camera(const cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;

  function disconnect_asi_camera(const cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;

  function analyse_asi_frame(const cameraId: ASI_CAM_ID): Integer; cdecl;

implementation
  const
{$ifdef CPU32}
    ASI_LIB = 'ASICamera2';
{$endif}
{$ifdef CPU64}
  {$ifdef MSWINDOWS}
    ASI_LIB = 'ASICamera2_x64';
  {$else}
    ASI_LIB = 'ASICamera2';
  {$endif}
{$endif}

  type
    ASI_IMG_TYPE = (last = -1, raw8 = 0, rgb24, raw16, y8);
    ASI_BAYER_PATTERN = (rg, bg, gr, gb);
    ASI_GUIDE_DIRECTION = (north, south, east, west);
    ASI_FLIP_STATUS = (none, horiz, vert, both);
    ASI_CAMERA_MODE = (last = -1, normal = 0,
                       trig_soft_edge, trig_rise_edge, trig_fall_edge,
                       trig_soft_level, trig_high_level, trig_low_level);
    ASI_TRIG_OUTPUT = (none = -1, pinA = 0, pinB);
    ASI_BOOL = (false, true);

    ASI_CAMERA_INFO = record
      Name: array[0..63] of Char;
      CameraID: ASI_CAM_ID;
      MaxHeight: ASI_LONG;
      MaxWidth: ASI_LONG;
      IsColorCam: ASI_BOOL;
      BayerPattern: ASI_BAYER_PATTERN;

      SupportBins: array[0..15] of ASI_INT;
      SupportVideoFormat: array[0..7] of ASI_IMG_TYPE;

      PixelSize: Double;

      MechnicalShutter: ASI_BOOL;
      ST4Port: ASI_BOOL;
      IsCoolerCam: ASI_BOOL;
      IsUSB3Host: ASI_BOOL;
      IsUSB3Camera: ASI_BOOL;
      ElecPerAdu: Single;
      BitDepth: ASI_INT;
      IsTriggerCam: ASI_BOOL;
      _unused: array[0..19] of byte;
    end;

    PASI_CAMERA_INFO = ^ASI_CAMERA_INFO;

  function ASIGetNumOfConnectedCameras: ASI_INT; cdecl; external ASI_LIB;
  function ASIGetCameraProperty(info: PASI_CAMERA_INFO; index: ASI_INT): ASI_ERROR; cdecl; external ASI_LIB;

  function ASIOpenCamera(cameraId: ASI_CAM_ID): ASI_ERROR; cdecl; external ASI_LIB;
  function ASIInitCamera(cameraId: ASI_CAM_ID): ASI_ERROR; cdecl; external ASI_LIB;
  function ASICloseCamera(cameraId: ASI_CAM_ID): ASI_ERROR; cdecl; external ASI_LIB;

  function find_asi_camera_by_index(const cameraIndex: ASI_INT; out cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;
  var
    cameraInfo: ASI_CAMERA_INFO;
  begin
    error := ASI_ERROR.general_error;
    Result := false;
    if (cameraIndex >= 0) and (cameraIndex < ASIGetNumOfConnectedCameras) then
    begin
      error := ASIGetCameraProperty(@cameraInfo, cameraIndex);

      if error = ASI_ERROR.success then
      begin
        cameraId := cameraInfo.CameraID;
        Result := true;
      end
      else
        cameraId := -1;
    end;
  end;

  function find_asi_camera_by_name(const name: PWideChar; out cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;
  var
    cameraInfo: ASI_CAMERA_INFO;
    cameraCount: Integer;
    cameraIndex: integer;
    cameraNameLen, searchLen: integer;

    function IsSame : Boolean;
    var
      I: integer;
    begin
      IsSame := false;
      for I := 0 to searchLen - 1 do
        if cameraInfo.Name[I] <> name[I] then
          Exit;

      IsSame := cameraInfo.Name[cameraNameLen] = char(0);
    end;
  begin
    error := ASI_ERROR.general_error;
    Result := false;
    cameraId := -1;

    cameraCount := ASIGetNumOfConnectedCameras;
    cameraNameLen := length(ASI_CAMERA_INFO.Name);
    searchLen := Length(name);

    if searchLen >= cameraNameLen then
      Exit;

    for cameraIndex := 0 to cameraCount - 1 do
    begin
      error := ASIGetCameraProperty(@cameraInfo, cameraIndex);

      if error = ASI_ERROR.success then
      begin
        Result := IsSame;
        WriteLn('Cam Len: ', cameraNameLen, ' Search Len: ', searchLen, 'Cam: ', cameraInfo.Name, ' search: ', string(name), ' res: ', Result);
        if Result then
        begin
          cameraId := cameraInfo.CameraID;
          Exit
        end
        else
          error := ASI_ERROR.invalid_id;
      end
    end
  end;

  function connect_asi_camera(const cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;
  begin
    Result := false;
    error := ASIOpenCamera(cameraId);
    if error = ASI_ERROR.success then
    begin
       error := ASIInitCamera(cameraId);
       Result := error = ASI_ERROR.success
    end;
  end;

  function disconnect_asi_camera(const cameraId: ASI_CAM_ID; out error: ASI_ERROR): Boolean; cdecl;
  begin
    error := ASICloseCamera(cameraId);
    Result := error = ASI_ERROR.success;
  end;

  function analyse_asi_frame(const cameraId: ASI_CAM_ID): Integer; cdecl;
  begin
    Result := -1
  end;

end.

