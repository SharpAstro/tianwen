library astap_lib;

{$mode objfpc}{$H+}

uses
  {$ifdef unix}
  cthreads,
  cmem, // the c memory manager is on some systems much faster for multi-threading
  {$endif}
  Classes,
  { you can add units after this }
  analysis,
  fits;

exports
  { FITS }
  analyse_fits;

begin
end.

