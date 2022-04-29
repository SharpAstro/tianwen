unit fits;

{$mode ObjFPC}{$H+}

interface

uses
  analysis;

  function prepare_ra(rax:double; sep:string):string; {radialen to text, format 24: 00 00.0 }
  function prepare_dec(decx:double; sep:string):string; {radialen to text, format 90d 00 00}
  procedure ra_text_to_radians(inp :string; out ra : double; out errorRA :boolean); {convert ra in text to double in radians}
  procedure dec_text_to_radians(inp :string; out dec : double; out errorDEC :boolean); {convert dec in text to double in radians}
  Function LeadingZero(w : integer) : String;

function load_fits(filen:string; out img_loaded2: image_array; out img_info: ImageInfo): boolean;{load fits file}

{* returns star count or error (negative) *}
function analyse_fits(const fileName: PWideChar; snr_min: double; max_stars: integer; out medianHFD, medianFWHM, background: double): Integer; cdecl;

implementation

uses
  Classes,
  Math,
  StrUtils,
  SysUtils;

type  byteX3    = array[0..2] of byte;
      byteXX3   = array[0..2] of word;
      byteXXXX3 = array[0..2] of single;

Function LeadingZero(w : integer) : String;
var
  s : String;
begin
  Str(w:0,s);
  if Length(s) = 1 then
    s := '0' + s;
  LeadingZero := s;
end;

function prepare_ra(rax:double; sep:string):string; {radialen to text, format 24: 00 00.0 }
 var
   h,m,s,ds  :integer;
 begin   {make from rax [0..pi*2] a text in array bericht. Length is 8 long}
  rax:=rax+pi*0.1/(24*60*60); {add 1/10 of half second to get correct rounding and not 7:60 results as with round}
  rax:=rax*12/pi; {make hours}
  h:=trunc(rax);
  m:=trunc((rax-h)*60);
  s:=trunc((rax-h-m/60)*3600);
  ds:=trunc((rax-h-m/60-s/3600)*36000);
  prepare_ra:=leadingzero(h)+sep+leadingzero(m)+'  '+leadingzero(s)+'.'+ansichar(ds+48);
end;



function prepare_dec(decx:double; sep:string):string; {radialen to text, format 90d 00 00}
 var
   g,m,s  :integer;
   sign   : ansichar;
begin {make from rax [0..pi*2] a text in array bericht. Length is 10 long}
  if decx<0 then sign:='-' else sign:='+';
  decx:=abs(decx)+pi/(360*60*60); {add half second to get correct rounding and not 7:60 results as with round}
  decx:=decx*180/pi; {make degrees}
  g:=trunc(decx);
  m:=trunc((decx-g)*60);
  s:=trunc((decx-g-m/60)*3600);
  prepare_dec:=sign+leadingzero(g)+sep+leadingzero(m)+'  '+leadingzero(s);
end;


procedure dec_text_to_radians(inp :string; out dec : double; out errorDEC :boolean); {convert dec in text to double in radians}
var
  decd,decm,decs :double;
  position1,position2,position3,error1,error2,error3,plusmin:integer ;
begin
  inp:= stringreplace(inp, ',', '.',[rfReplaceAll]);
  inp:= stringreplace(inp, ':', ' ',[rfReplaceAll]);
  inp:= stringreplace(inp, 'd', ' ',[rfReplaceAll]);
  inp:= stringreplace(inp, 'm', ' ',[rfReplaceAll]);
  inp:= stringreplace(inp, 's', ' ',[rfReplaceAll]);
  inp:= stringreplace(inp, '°', ' ',[rfReplaceAll]);
  inp:= stringreplace(inp, '  ', ' ',[rfReplaceAll]);
  inp:= stringreplace(inp, '  ', ' ',[rfReplaceAll]);
  inp:=trim(inp)+' ';
  if pos('-',inp)>0 then plusmin:=-1 else plusmin:=1;

  position1:=pos(' ',inp);
  val(copy(inp,1,position1-1),decd,error1);


  position2:=posex(' ',inp,position1+1);
  if position2-position1>1 then {decm available}
  begin
    val(copy(inp,position1+1,position2-position1-1),decm,error2);

    {decm found try decs}
    position3:=posex(' ',inp,position2+1);
    if position3-position2>1 then val( copy(inp,position2+1,position3-position2-1),decs,error3)
       else begin decs:=0;error3:=0;end;
  end
  else
    begin decm:=0;error2:=0;decs:=0; error3:=0; end;

  dec:=plusmin*(abs(decd)+decm/60+decs/3600)*pi/180;
  errorDEC:=((error1<>0) or (error2>1) or (error3<>0));
end;


procedure ra_text_to_radians(inp :string; out ra : double; out errorRA :boolean); {convert ra in text to double in radians}
var
  rah,ram,ras,plusmin :double;
  position1,position2,position3,error1,error2,error3:integer;
begin

  inp:= stringreplace(inp, ',', '.',  [rfReplaceAll]);
  inp:= stringreplace(inp, ':', ' ',  [rfReplaceAll]);
  inp:= stringreplace(inp, 'h', ' ',  [rfReplaceAll]);
  inp:= stringreplace(inp, 'm', ' ',  [rfReplaceAll]);
  inp:= stringreplace(inp, 's', ' ',  [rfReplaceAll]);
  inp:= stringreplace(inp, '  ', ' ', [rfReplaceAll]);
  inp:= stringreplace(inp, '  ', ' ', [rfReplaceAll]);

  inp:=trim(inp)+' ';
  if pos('-',inp)>0 then plusmin:=-1 else plusmin:=1;

  position1:=pos(' ',inp);
  val(copy(inp,1,position1-1),rah,error1);

  position2:=posex(' ',inp,position1+1);
  if position2-position1>1 then {ram available}
  begin
    val(copy(inp,position1+1,position2-position1-1),ram,error2);

    {ram found try ras}
    position3:=posex(' ',inp,position2+1);
    if position3-position2>1 then val( copy(inp,position2+1,position3-position2-1),ras,error3)
       else begin ras:=0;error3:=0;end;
  end
  else
    begin ram:=0;error2:=0; ras:=0; error3:=0; end;

  ra:=plusmin*(abs(rah)+ram/60+ras/3600)*pi/12;
  errorRA:=((error1<>0) or (error2>1) or (error3<>0) or (ra>2*pi));
end;

function load_fits(filen:string; out img_loaded2: image_array; out img_info: ImageInfo): boolean;{load fits file}
const
  bufwide=1024*120;{buffer size in bytes}
var
  header    : array[0..2880] of ansichar;
  i,j,k,naxis1, reader_position              : integer;
  dummy                                             : double;
  col_float,bscale,measured_max,scalefactor  : single;
  bzero                       : integer;{zero shift. For example used in AMT, Tricky do not use int64,  maxim DL writes BZERO value -2147483647 as +2147483648 !! }
  aline                       : ansistring;
  rgbdummy           : byteX3;

  word16             : word;   {for 16 signed integer}
  int_16             : smallint absolute word16;{for 16 signed integer}

  x_longword  : longword;
  x_single    : single absolute x_longword;{for conversion 32 bit "big-endian" data}
  int_32      : integer absolute x_longword;{for 32 bit signed integer}

  x_qword     : qword;
  x_double    : double absolute x_qword;{for conversion 64 bit "big-endian" data}
  int_64      : int64 absolute x_qword;{for 64 bit signed integer}

  TheFile3  : tfilestream;
  Reader    : TReader;
  fitsbuffer : array[0..bufwide] of byte;{buffer for 8 bit FITS file}
  fitsbuffer2: array[0..round(bufwide/2)] of word absolute fitsbuffer;{buffer for 16 bit FITS file}
  fitsbufferRGB: array[0..trunc(bufwide/3)] of byteX3 absolute fitsbuffer;{buffer for 8 bit RGB FITS file}
  fitsbuffer4: array[0..round(bufwide/4)] of longword absolute fitsbuffer;{buffer for floating bit ( -32) FITS file}
  fitsbuffer8: array[0..trunc(bufwide/8)] of int64 absolute fitsbuffer;{buffer for floating bit ( -64) FITS file}
  fitsbufferSINGLE: array[0..round(bufwide/4)] of single absolute fitsbuffer;{buffer for floating bit ( -32) FITS file}
  fitsbufferDouble: array[0..round(bufwide/8)] of double absolute fitsbuffer;{buffer for floating bit ( -64) FITS file}

  hist_range  {range histogram 255 or 65535 or streched} : integer=255;
  naxis  : integer=2;{number of dimensions}
  naxis3 : integer=1;{number of colors}
  ra0 : double=0;
  dec0: double=0; {plate center values}
  crpix1: double=0;{reference pixel}
  crpix2: double=0;
  cdelt1: double=0;{deg/pixel for x}
  cdelt2: double=0;
  xpixsz: double=0;//Pixel Width in microns (after binning)
  ypixsz: double=0;//Pixel height in microns (after binning)
  focallen: double=0;
  Xbinning,Ybinning    : integer;
  size_backup,index_backup    : integer;{number of backup images for ctrl-z, numbered 0,1,2,3}
  crota2,crota1                      : double; {image rotation at center in degrees}
  cd1_1,cd1_2,cd2_1,cd2_2 :double;
  ra_radians,dec_radians, pixel_size : double;
  ra_mount,dec_mount          : double; {telescope ra,dec}
  datamin_org, datamax_org :double;
  ra1  : string='0';
  dec1 : string='0';

  simple,image,error1 : boolean;
const
  end_record : boolean=false;

     procedure close_fits_file; inline;
     begin
        Reader.free;
        TheFile3.free;
     end;

     Function validate_double:double;{read floating point or integer values}
     var t : string[20];
         r,err : integer;
     begin
       t:='';
       r:=I+10;{position 11 equals 10}
       while ((header[r]<>'/') and (r<=I+29) {pos 30}) do {'/' check is strictly not necessary but safer}
       begin  {read 20 characters max, position 11 to 30 in string, position 10 to 29 in pchar}
         if header[r]<>' ' then t:=t+header[r];
         inc(r);
       end;
       val(t,result,err);
     end;

     Function get_string:string;{read string values}
     var  r: integer;
     begin
       result:='';
       r:=I+11;{pos12, single quotes should for fix format should be at position 11 according FITS standard 4.0, chapter 4.2.1.1}
       repeat
         result:=result+header[r];
         inc(r);
       until ((header[r]=#39){last quote} or (r>=I+79));{read string up to position 80 equals 79}
     end;

     Function get_as_string:string;{read float as string values. Universal e.g. for latitude and longitude which could be either string or float}
     var  r: integer;
     begin
       result:='';
       r:=I+11;{pos12, single quotes should for fix format should be at position 11 according FITS standard 4.0, chapter 4.2.1.1}
       repeat
         result:=result+header[r];
         inc(r);
       until ((header[r]=#39){last quote} or (r>=I+30));{read string up to position 30}
     end;

begin
 {some house keeping}
 result:=false; {assume failure}
 {house keeping done}

 try
   TheFile3:=tfilestream.Create( filen, fmOpenRead or fmShareDenyWrite);
 except
   exit;
 end;

 Reader := TReader.Create (theFile3,500*2880);{number of records. Buffer but not speed difference between 6*2880 and 1000*2880}
 {thefile3.size-reader.position>sizeof(hnskyhdr) could also be used but slow down a factor of 2 !!!}

 {Reset variables for case they are not specified in the file}
//  crota2:=99999;{just for the case it is not available, make it later zero}
//  crota1:=99999;
 ra0:=0;
 dec0:=0;
 ra_mount:=99999;
 dec_mount:=99999;
 cdelt1:=0;
 cdelt2:=0;
 xpixsz:=0;
 ypixsz:=0;
 focallen:=0;
 cd1_1:=0;{just for the case it is not available}
 cd1_2:=0;{just for the case it is not available}
 cd2_1:=0;{just for the case it is not available}
 cd2_2:=0;{just for the case it is not available}
 xbinning:=1;{normal}
 ybinning:=1;
 ra1:='';
 dec1:='';

 naxis:=-1;
 naxis1:=0;
 naxis3:=1;
 bzero:=0;{just for the case it is not available. 0.0 is the default according https://heasarc.gsfc.nasa.gov/docs/fcg/standard_dict.html}
 bscale:=1;
 datamin_org:=0;

 measured_max:=0;

 reader_position:=0;
 repeat {header, 2880 bytes loop}

   I:=0;
   try
     reader.read(header[I],2880);{read file header, 2880 bytes}
     inc(reader_position,2880);
     if ((reader_position=2880) and (header[0]='S') and (header[1]='I')  and (header[2]='M') and (header[3]='P') and (header[4]='L') and (header[5]='E') and (header[6]=' ')) then
     begin
       simple:=true;
       image:=true;
     end;
     if simple=false then
     begin
       close_fits_file;
       exit;
     end; {should start with SIMPLE  =,  MaximDL compressed files start with SIMPLE‚=”}
   except
     close_fits_file;
     exit;
   end;

   repeat  {loop for 80 bytes in 2880 block}
     SetString(aline, Pansichar(@header[i]), 80);{convert header line to string}

     if ((header[i]='N') and (header[i+1]='A')  and (header[i+2]='X') and (header[i+3]='I') and (header[i+4]='S')) then {naxis}
     begin
       if (header[i+5]=' ') then
           naxis:=round(validate_double)
       else    {NAXIS number of colors}
       if (header[i+5]='1') then begin naxis1:=round(validate_double); img_info.img_width := naxis1; end else {NAXIS1 pixels}
       if (header[i+5]='2') then img_info.img_height :=round(validate_double) else   {NAXIS2 pixels}
       if (header[i+5]='3') then
       begin
          naxis3:=round(validate_double); {NAXIS3 number of colors}
          if ((naxis=3) and (naxis1=3)) {naxis1} then  {type NAXIS = 3 / Number of dimensions
                                    NAXIS1 = 3 / Number of Colors
                                    NAXIS2 = 382 / Row length
                                    NAXIS3 = 255 / Number of rows}
                     begin   {RGB fits with naxis1=3, treated as 24 bits coded pixels in 2 dimensions}
                       img_info.img_width  := img_info.img_height;
                       img_info.img_height := naxis3;
                       naxis3:=1;
                     end;
        end;
     end;


     if image then {image specific header}
     begin {read image header}
       if ((header[i]='B') and (header[i+1]='I')  and (header[i+2]='T') and (header[i+3]='P') and (header[i+4]='I') and (header[i+5]='X')) then
         img_info.bit_depth := round(validate_double);{BITPIX, read integer using double routine}

       if (header[i]='B') then
       begin
         if ( (header[i+1]='Z')  and (header[i+2]='E') and (header[i+3]='R') and (header[i+4]='O') ) then
         begin
            dummy:=validate_double;
            if dummy>2147483647 then
            bzero:=-2147483648
            else
            bzero:=round(dummy); {Maxim DL writes BZERO value -2147483647 as +2147483648 !! }
           {without this it would have worked also with error check off}
         end
         else
         if ( (header[i+1]='S')  and (header[i+2]='C') and (header[i+3]='A') and (header[i+4]='L') ) then
          begin
             bscale:=validate_double; {rarely used. Normally 1}
          end;
       end;

       if ((header[i]='X') and (header[i+1]='B')  and (header[i+2]='I') and (header[i+3]='N') and (header[i+4]='N') and (header[i+5]='I')) then
                xbinning:=round(validate_double);{binning}
       if ((header[i]='Y') and (header[i+1]='B')  and (header[i+2]='I') and (header[i+3]='N') and (header[i+4]='N') and (header[i+5]='I')) then
                ybinning:=round(validate_double);{binning}

       if ((header[i]='C') and (header[i+1]='D')  and (header[i+2]='E') and (header[i+3]='L') and (header[i+4]='T')) then {cdelt1}
       begin
         if header[i+5]='1' then cdelt1:=validate_double else{deg/pixel for RA}
         if header[i+5]='2' then cdelt2:=validate_double;    {deg/pixel for DEC}
       end;
       if ( ((header[i]='S') and (header[i+1]='E')  and (header[i+2]='C') and (header[i+3]='P') and (header[i+4]='I') and (header[i+5]='X')) or     {secpix1/2}
            ((header[i]='S') and (header[i+1]='C')  and (header[i+2]='A') and (header[i+3]='L') and (header[i+4]='E') and (header[i+5]=' ')) or     {SCALE value for SGP files}
            ((header[i]='P') and (header[i+1]='I')  and (header[i+2]='X') and (header[i+3]='S') and (header[i+4]='C') and (header[i+5]='A')) ) then {pixscale}
       begin
         if cdelt2=0 then
             begin cdelt2:=validate_double/3600; {deg/pixel for RA} cdelt1:=cdelt2; end; {no CDELT1/2 found yet, use alternative}
       end;

       if ((header[i]='X') and (header[i+1]='P')  and (header[i+2]='I') and (header[i+3]='X') and (header[i+4]='S') and (header[i+5]='Z')) then {xpixsz}
              xpixsz:=validate_double;{Pixel Width in microns (after binning), maxim DL keyword}
       if ((header[i]='Y') and (header[i+1]='P')  and (header[i+2]='I') and (header[i+3]='X') and (header[i+4]='S') and (header[i+5]='Z')) then {xpixsz}
            ypixsz:=validate_double;{Pixel Width in microns (after binning), maxim DL keyword}

      if ((header[i]='F') and (header[i+1]='O')  and (header[i+2]='C') and (header[i+3]='A') and (header[i+4]='L') and (header[i+5]='L')) then  {focall}
           focallen:=validate_double;{Focal length of telescope in mm, maxim DL keyword}

       if ((header[i]='C') and (header[i+1]='R')  and (header[i+2]='V') and (header[i+3]='A') and (header[i+4]='L')) then {crval1/2}
       begin
         if (header[i+5]='1') then  ra0:=validate_double*pi/180; {ra center, read double value}
         if (header[i+5]='2') then  dec0:=validate_double*pi/180; {dec center, read double value}
       end;
       if ((header[i]='R') and (header[i+1]='A')  and (header[i+2]=' ')) then  {ra}
       begin
         ra_mount:=validate_double*pi/180;
         if ra0=0 then ra0:=ra_mount; {ra telescope, read double value only if crval is not available}
       end;
       if ((header[i]='D') and (header[i+1]='E')  and (header[i+2]='C') and (header[i+3]=' ')) then {dec}
       begin
         dec_mount:=validate_double*pi/180;
         if dec0=0 then dec0:=dec_mount; {ra telescope, read double value only if crval is not available}
       end;


       if ((header[i]='O') and (header[i+1]='B')  and (header[i+2]='J')) then
       begin
         if  ((header[i+3]='C') and (header[i+4]='T')) then {objctra, objctdec}
         begin
           if ((header[i+5]='R') and (header[i+6]='A') and (ra_mount>=999) {ra_mount value is unfilled, preference for keyword RA}) then
           begin
             ra1:=get_string;
           end
           else
           if ((header[i+5]='D') and (header[i+6]='E') and (dec_mount>=999){dec_mount value is unfilled, preference for keyword DEC}) then
           begin
             dec1:=get_string;
           end;
         end;
       end;

       if ((header[i]='C') and (header[i+1]='D')) then
       begin
         if ((header[i+2]='1') and (header[i+3]='_') and (header[i+4]='1')) then   cd1_1:=validate_double;
         if ((header[i+2]='1') and (header[i+3]='_') and (header[i+4]='2')) then   cd1_2:=validate_double;
         if ((header[i+2]='2') and (header[i+3]='_') and (header[i+4]='1')) then   cd2_1:=validate_double;
         if ((header[i+2]='2') and (header[i+3]='_') and (header[i+4]='2')) then   cd2_2:=validate_double;
       end;


     end; {image header}

     end_record:=((header[i]='E') and (header[i+1]='N')  and (header[i+2]='D') and (header[i+3]=' '));{end of header. Note keyword ENDIAN exist, so test space behind END}
     inc(i,80);{go to next 80 bytes record}

   until ((i>=2880) or (end_record)); {loop for 80 bytes in 2880 block}
 until end_record; {header, 2880 bytes loop}


 if naxis<2 then
 begin
   result:=false; {no image}
   image:=false;
 end;


 if image then {read image data #########################################}
 begin
   if ((naxis=3) and (naxis1=3)) then
   begin
      img_info.bit_depth := 24; {threat RGB fits as 2 dimensional with 24 bits data}
      naxis3:=3; {will be converted while reading}
   end;

   if ((ra0<>0) or (dec0<>0)) then
   begin
     ra1:=prepare_ra(ra0,' ');
     dec1:=prepare_dec(dec0,' ');
   end
   else
   if ra1<>'' then
   begin
     ra_text_to_radians ( ra1 ,ra0,error1); {convert ra text to ra0 in radians}
     dec_text_to_radians( dec1,dec0,error1); {convert dec text to dec0 in radians}
   end;

   if cdelt2=0 then {simple code for astap-cli only}
   begin
     if cd1_1=0 then  {no scale, try to fix it}
     begin
      if ((focallen<>0) and (xpixsz<>0)) then
         cdelt2:=180/(pi*1000)*xpixsz/focallen; {use maxim DL key word. xpixsz is including binning}
     end
     else
     cdelt2:=sqrt(sqr(cd1_2)+sqr(cd2_2));
   end;

   {############################## read image}
   i:=round(bufwide/(abs(img_info.bit_depth / 8)));{check if buffer is wide enough for one image line}
   if img_info.img_width > i then
   begin
     close_fits_file;
     exit;
   end;

   setlength(img_loaded2,naxis3,img_info.img_width,img_info.img_height);

   if img_info.bit_depth = 16 then
   for k:=0 to naxis3-1 do {do all colors}
   begin
     For j:=0 to img_info.img_height - 1 do
     begin
       try reader.read(fitsbuffer, img_info.img_width * 2);except; end; {read file info}
       for i:=0 to img_info.img_width - 1 do
       begin
         word16:=swap(fitsbuffer2[i]);{move data to wo and therefore sign_int}
         col_float:=int_16*bscale + bzero; {save in col_float for measuring measured_max}
         img_loaded2[k,i,j]:=col_float;
         if col_float>measured_max then measured_max:=col_float;{find max value for image. For for images with 0..1 scale or for debayer}
       end;
     end;
   end {colors naxis3 times}
   else
   if img_info.bit_depth = -32 then
   for k:=0 to naxis3-1 do {do all colors}
   begin
     For j:=0 to img_info.img_height -1 do
     begin
       try reader.read(fitsbuffer,img_info.img_width * 4);except; end; {read file info}
       for i:=0 to img_info.img_width - 1 do
       begin
         x_longword:=swapendian(fitsbuffer4[i]);{conversion 32 bit "big-endian" data, x_single  : single absolute x_longword; }
         col_float:=x_single*bscale+bzero; {int_IEEE, swap four bytes and the read as floating point}
         if isNan(col_float) then col_float:=measured_max;{not a number prevent errors, can happen in PS1 images with very high floating point values}
         img_loaded2[k,i,j]:=col_float;{store in memory array}
         if col_float>measured_max then measured_max:=col_float;{find max value for image. For for images with 0..1 scale or for debayer}
       end;
     end;
   end {colors naxis3 times}
   else
   if img_info.bit_depth = 8 then
   for k:=0 to naxis3-1 do {do all colors}
   begin
     For j:=0 to img_info.img_height - 1 do
     begin
       try reader.read(fitsbuffer,img_info.img_width);except; end; {read file info}
       for i:=0 to img_info.img_width - 1 do
       begin
         img_loaded2[k,i,j]:=(fitsbuffer[i]*bscale + bzero);
       end;
     end;
   end {colors naxis3 times}
   else
   if img_info.bit_depth = 24 then
   For j:=0 to img_info.img_height - 1 do
   begin
     try reader.read(fitsbuffer,img_info.img_width * 3);except; end; {read file info}
     for i:=0 to img_info.img_width - 1 do
     begin
       rgbdummy:=fitsbufferRGB[i];{RGB fits with naxis1=3, treated as 24 bits coded pixels in 2 dimensions}
       img_loaded2[0,i,j]:=rgbdummy[0];{store in memory array}
       img_loaded2[1,i,j]:=rgbdummy[1];{store in memory array}
       img_loaded2[2,i,j]:=rgbdummy[2];{store in memory array}
     end;
   end
   else
   if img_info.bit_depth = +32 then
   for k:=0 to naxis3-1 do {do all colors}
   begin
     For j:=0 to img_info.img_height - 1 do
     begin
       try reader.read(fitsbuffer,img_info.img_width * 4);except; end; {read file info}
       for i:=0 to img_info.img_width - 1 do
       begin
         col_float:=(swapendian(fitsbuffer4[i])*bscale+bzero)/(65535);{scale to 0..64535 or 0..1 float}
                        {Tricky do not use int64 for BZERO,  maxim DL writes BZERO value -2147483647 as +2147483648 !!}
         img_loaded2[k,i,j]:=col_float;{store in memory array}
         if col_float>measured_max then measured_max:=col_float;{find max value for image. For for images with 0..1 scale or for debayer}
       end;
     end;
   end {colors naxis3 times}
   else
   if img_info.bit_depth = -64 then
   for k:=0 to naxis3-1 do {do all colors}
   begin
     For j:=0 to img_info.img_height - 1 do
     begin
       try reader.read(fitsbuffer,img_info.img_width * 8);except; end; {read file info}
       for i:=0 to img_info.img_width - 1 do
       begin
         x_qword:=swapendian(fitsbuffer8[i]);{conversion 64 bit "big-endian" data, x_double    : double absolute x_int64;}
         col_float:=x_double*bscale + bzero; {int_IEEE, swap four bytes and the read as floating point}
         img_loaded2[k,i,j]:=col_float;{store in memory array}
         if col_float>measured_max then measured_max:=col_float;{find max value for image. For for images with 0..1 scale or for debayer}
       end;
     end;
   end; {colors naxis3 times}

   {rescale if required}
   if ((img_info.bit_depth <= -32){-32 or -64} or (img_info.bit_depth = +32)) then
   begin
     scalefactor:=1;
     if ((measured_max<=1.01) or (measured_max>65535)) then scalefactor:=65535/measured_max; {rescale 0..1 range float for GIMP, Astro Pixel Processor, PI files, transfer to 0..65535 float}
                                                                                             {or if values are above 65535}
     if scalefactor<>1 then {not a 0..65535 range, rescale}
     begin
       for k:=0 to naxis3-1 do {do all colors}
         for j:=0 to img_info.img_height - 1 do
           for i:=0 to img_info.img_width - 1 do
             img_loaded2[k,i,j]:= img_loaded2[k,i,j]*scalefactor;
       datamax_org:=65535;
     end
     else
       datamax_org:=measured_max;

   end
   else
   if img_info.bit_depth = 8 then datamax_org := 255 {not measured}
   else
   if img_info.bit_depth = 24 then
   begin
     datamax_org:=255;
     img_info.bit_depth := 8; {already converted to array with separate colour sections}
   end
   else {16 bit}
     datamax_org:=measured_max;{most common. It set for nrbits=24 in beginning at 255}

   result := true;
   reader_position := reader_position + img_info.img_width * img_info.img_height * (abs(img_info.bit_depth) div 8)
 end; {image block}
 close_fits_file;
end;

function analyse_fits(
  const fileName: PWideChar;
  snr_min: double;
  max_stars: integer;
  out medianHFD, medianFWHM, background: double): Integer; cdecl;
var
  img: image_array;
  img_info: ImageInfo;
begin
  if load_fits(fileName, img, img_info) then
  begin
     Result := analyse_image(img, img_info, snr_min, max_stars, medianHFD, medianFWHM, background);
  end
  else
  begin
     medianHFD  := -1;
     medianFWHM := -1;
     background := -1;
     Result     := -1;
  end;
end;

end.

