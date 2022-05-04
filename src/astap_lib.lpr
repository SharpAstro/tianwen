library astap_lib;

{$mode objfpc}{$H+}

uses
  Classes,
  { you can add units after this }
  analysis,
  fits,
  zwo;

exports
  { FITS }
  analyse_fits,

  { ZWO }
  find_asi_camera_by_index,
  find_asi_camera_by_name,
  connect_asi_camera,
  disconnect_asi_camera,
  analyse_asi_frame;

begin
end.

