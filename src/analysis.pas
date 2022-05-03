unit analysis;

{$mode ObjFPC}{$H+}

interface

type
  image_array = array of array of array of double;
  histogram_array = array[0..65535] of integer;{red,green,blue,count}
  colored_stat_array = array[0..2] of double;

  THistogramStats = record
    red: integer;
    green: integer;
    blue: integer;
    mean : colored_stat_array;
  end;

  TImageInfo = record
    img_width: integer;
    img_height: integer;
    bit_depth: integer;
  end;
  PImageInfo = ^TImageInfo;

  { get histogram of each colour, and their mean and total values }
  function get_hist(colour:integer; const img :image_array; const img_info: TImageInfo; out histogram_stats: THistogramStats) : histogram_array;

  { get background and star level from peek histogram }
  procedure get_background(colour: integer; const img :image_array; const img_info: TImageInfo; out background, starlevel: double; out noise_level: colored_stat_array);

  { Fast quick sort. Sorts elements in the array list with indices between lo and hi }
  procedure QuickSort(var A: array of double; iLo, iHi: Integer);

  { get median of an array of double. Taken from CCDciel code but slightly modified }
  function SMedian(list: array of double; leng: integer): double;

  { calculate star HFD and FWHM, SNR, xc and yc are center of gravity. All x,y coordinates in array[0..] positions }
  procedure HFD(const img: image_array; const img_info: PImageInfo; x1,y1,rs {boxsize}: integer; out hfd1,star_fwhm,snr{peak/sigma noise}, flux,xc,yc:double);

  { find background, number of stars, median HFD, returns star count }
  function analyse_image(const img: image_array; const img_info: TImageInfo; snr_min: double; max_stars: integer; out hfd_median, fwhm_median, background : double): integer;

implementation

uses
  Math,
  Classes;

type
  TBitMatrix = array of TBits;
  double_array = array of double;

  TImgAnalyseContext = record
    box_size: integer;
    max_stars: integer;
    background: double;
    thread_count: integer;
    snr_min: double;
  end;
  PImgAnalyseContext = ^TImgAnalyseContext;

  THistThread = class(TThread)
  private
    fImage: image_array;
    fHistogramValues: histogram_array;
    fColor: integer;
    fWidthStart, fWidthEnd: integer;
    fHeightStart, fHeightEnd: integer;
    fHisTotal : integer;
    fTotalValue: double;

  protected
    procedure Execute; override;

  public
    constructor Create(
      const img: image_array;
      const color: integer;
      const w_start, w_end, h_start, h_end: integer);

    property HisTotal: integer read FHisTotal;
    property TotalValue: double read FTotalValue;
    property HisValues: histogram_array read fHistogramValues;

  end;

  TImgAnalyseThread = class(TThread)
  private
    fContext: PImgAnalyseContext;
    fImage: image_array;
    fImageSA: TBitMatrix;
    fImageInfo: PImageInfo;
    fHFD_list, fFWHM_list: array of double;
    fStarCounter: integer;
    fLength: integer;
    fYStart, fYEnd: integer;
    fDetectionLevel : double;

  protected
    procedure Execute; override;

  public
    constructor Create(
      const img: image_array;
      const img_sa: TBitMatrix;
      const img_info: PImageInfo;
      const context: PImgAnalyseContext;
      const y_start, y_end: integer;
      const detection_level: double;
      const startSuspended: boolean);

    destructor Destroy; override;

    property StarCounter : integer read fStarCounter;
    property HFDList : double_array read fHFD_list;
    property FWHMList : double_array read fFWHM_list;
  end;

constructor THistThread.Create(
  const img: image_array;
  const color: integer;
  const w_start, w_end, h_start, h_end: integer);
var
  i: integer;
begin
  inherited Create(False);

  fImage := img;
  fColor := color;
  fWidthStart := w_start;
  fWidthEnd := w_end;
  fHeightStart := h_start;
  fHeightEnd := h_end;
  fHisTotal := 0;
  fTotalValue := 0;

  for i:=0 to 65535 do
    fHistogramValues[i] := 0;{clear histogram}
end;

procedure THistThread.Execute;
var
  h, w, col : integer;
begin
  For h := fHeightStart to fHeightEnd - 1 do
  begin
    for w := fWidthStart to fWidthEnd - 1 do
    begin
      col:=round(fImage[fColor, w, h]);
      if ((col>=1) and (col<65000)) then {ignore black overlap areas and bright stars}
      begin
        Inc(fHistogramValues[col]);
        Inc(fHisTotal);

        fTotalValue := fTotalValue + col
      end;
    end;{h}
  end;{w}
end;

constructor TImgAnalyseThread.Create(
  const img: image_array;
  const img_sa: TBitMatrix;
  const img_info: PImageInfo;
  const context: PImgAnalyseContext;
  const y_start, y_end: integer;
  const detection_level: double;
  const startSuspended: boolean);
begin
  inherited Create(startSuspended);

  fImage := img;
  fImageSA := img_sa;
  fImageInfo := img_info;
  fContext := context;
  fDetectionLevel := detection_level;
  fStarCounter := 0;
  fYStart := y_start;
  fYEnd := y_end;
  fLength := Round(context^.max_stars / context^.thread_count); { start list length with star target }
  SetLength(fHFD_list, fLength);
  SetLength(fFWHM_list, fLength);

  // WriteLn('initialise thread: ', startSuspended, ' y-start: ', fYStart, ' y-end: ', fYEnd);
end;

destructor TImgAnalyseThread.Destroy;
begin
  fHFD_list := nil;
  fFWHM_list := nil;
  inherited Destroy;
end;

procedure TImgAnalyseThread.Execute;
var
  { copies for efficency }
  background, detection_level : double;
  box_size                    : integer;
  snr_min                     : double;
  img_width, img_height       : integer;
  { mutable vars }
  fitsX, fitsY, diam, m, n, xci, yci, sqr_diam, i, j  : integer;
  hfd1, star_fwhm, snr, flux, xc, yc                  : double;
begin
  background      := fContext^.background;
  detection_level := fDetectionLevel;
  box_size        := fContext^.box_size;
  snr_min         := fContext^.snr_min;
  img_width       := fImageInfo^.img_width;
  img_height      := fImageInfo^.img_height;

  WriteLn('starting thread: y-start: ', fYStart, ' y-end: ', fYEnd);

  for fitsY := fYStart to fYEnd - 1 do
  begin
    for fitsX := 0 to img_width - 1  do
    begin
      if (not (fImageSA[fitsY][fitsX]){star free area}
        and (fImage[0, fitsX, fitsY] - background > detection_level))
      then {new star. For analyse used sigma is 5, so not too low.}
      begin
        HFD(fImage, fImageInfo, fitsX, fitsY, box_size, hfd1, star_fwhm, snr, flux, xc, yc); { star HFD and FWHM }
        if ((hfd1 <= 30) and (snr > snr_min) and (hfd1 > 0.8) { two pixels minimum } ) then
        begin
          fHFD_list[fStarCounter]  := hfd1;
          fFWHM_list[fStarCounter] := star_fwhm;

          inc(fStarCounter);
          if fStarCounter >= fLength then
          begin
            fLength := fLength + round(fContext^.max_stars / fContext^.thread_count);
            SetLength(fHFD_list, fLength);
            SetLength(fFWHM_list, fLength)
          end;

          diam:=round(3.0 * hfd1);{for marking star area. Emperical a value between 2.5*hfd and 3.5*hfd gives same performance. Note in practise a star PSF has larger wings then predicted by a Gaussian function}
          sqr_diam:=sqr(diam);
          xci:=round(xc);{star center as integer}
          yci:=round(yc);
          for n:=-diam to +diam do {mark the whole circular star area width diameter "diam" as occupied to prevent double detections}
            for m:=-diam to +diam do
            begin
              j:=n+yci;
              i:=m+xci;
              if ((j>=0) and (i>=0) and (j< img_height) and (i < img_width) and ( (sqr(m)+sqr(n)) <= sqr_diam)) then
                fImageSA[j][i] := true;
            end;
        end;
      end;
    end;
  end;

end;

function get_hist(colour:integer; const img :image_array; const img_info: TImageInfo; out histogram_stats: THistogramStats) : histogram_array;
var
  his_threads : array[1..1] of THistThread;
  his_thread : THistThread;
  his_total, offsetW, offsetH  : integer;
  i, j, startH, endH, stepSize : integer;
  total_value: double;
begin
  if colour+1>length(img) then {robust detection, in case binning is applied and image is mono}
    colour:=0; {used red only}

  for i:=0 to 65535 do
    Result[i] := 0;{clear histogram of specified colour}

  his_total:=0;
  total_value:=0;

  offsetW:=trunc(img_info.img_width * 0.042); {if Libraw is used, ignored unused sensor areas up to 4.2%}
  offsetH:=trunc(img_info.img_height * 0.015); {if Libraw is used, ignored unused sensor areas up to 1.5%}

  stepSize := trunc((img_info.img_height - (offsetH * 2)) / high(his_threads));
  startH := offsetH;
  for I := low(his_threads) to high(his_threads) do
  begin
    if I = high(his_threads) then
      endH := img_info.img_height - offsetH
    else
      endH := startH + stepSize;

    writeln(' his thread: ', I, ' w-s: ', offsetW, ' w-e: ', img_info.img_width - offsetW, ' h-s: ', startH, ' h-e: ', endH);

    his_threads[I] := THistThread.Create(img, colour, offsetW, img_info.img_width - offsetW, startH, endH);
    startH := endH;
  end;

  for I := low(his_threads) to high(his_threads) do
  begin
    his_thread := his_threads[I];
    his_thread.WaitFor;
    writeln(' his thread: ', I, ' finished: histotal: ', his_thread.HisTotal, ' total value: ', his_thread.TotalValue);

    inc(his_total, his_thread.HisTotal);
    total_value := total_value + his_thread.TotalValue;

    for J := 0 to 65535 do
      inc(Result[I], his_thread.HisValues[i]);

    his_thread.Free;
  end;

  if colour=0 then
    histogram_stats.red := his_total
  else if colour=1 then
    histogram_stats.green := his_total
  else
    histogram_stats.blue := his_total;

  histogram_stats.mean[colour] := total_value / (his_total + 1);

  WriteLn('high(threads): ', high(his_threads) , ' total value: ', total_value, ' his total: ', his_total, ' mean: ', histogram_stats.mean[colour]);

end;


procedure get_background(colour: integer; const img :image_array; const img_info: TImageInfo; out background, starlevel: double; out noise_level: colored_stat_array); {get background and star level from peek histogram}
var
  i, pixels,max_range,above,his_total, fitsX, fitsY,counter,stepsize, iterations : integer;
  value,sd, sd_old : double;
  histogram_stats : THistogramStats;
  histogram : histogram_array;
begin
  histogram := get_hist(colour, img, img_info, histogram_stats);{get histogram of img_loaded and his_total}

  background:=img[0,0,0];{define something for images containing 0 or 65535 only}

  {find peak in histogram which should be the average background}
  pixels:=0;
  max_range := round(histogram_stats.mean[colour]); {mean value from histogram}
  for i := 1 to max_range do {find peak, ignore value 0 from oversize}
    if histogram[i] > pixels then {find colour peak}
    begin
      pixels:= histogram[i];
      background:=i;
    end;

  {check alternative mean value}
  if histogram_stats.mean[colour]>1.5*background {1.5* most common} then  {changed from 2 to 1.5 on 2021-5-29}
  begin
    background := histogram_stats.mean[colour];{strange peak at low value, ignore histogram and use mean}
  end;

  {calculate star level}
  if ((img_info.bit_depth = 8) or (img_info.bit_depth = 24)) then max_range:= 255 else max_range:=65001 {histogram runs from 65000};{8 or 16 / -32 bit file}
  i:=max_range;
  starlevel:=0;
  above:=0;

  if colour=1 then
    his_total := histogram_stats.green
  else
  if colour=2 then
    his_total := histogram_stats.blue
  else
    his_total := histogram_stats.red;

  while ((starlevel=0) and (i>background+1)) do {find star level 0.003 of values}
  begin
     dec(i);
     above:=above+histogram[i];
     if above>0.001*his_total then starlevel:=i;
  end;
  if starlevel <= background then
    starlevel := background+1 {no or very few stars}
  else
    starlevel := starlevel-background-1;{star level above background. Important subtract 1 for saturated images. Otherwise no stars are detected}

  {calculate noise level}
  stepsize:=round(img_info.img_height/71);{get about 71x71=5000 samples. So use only a fraction of the pixels}
  if odd(stepsize)=false then stepsize:=stepsize+1;{prevent problems with even raw OSC images}

  sd:=99999;
  iterations:=0;
  repeat  {repeat until sd is stable or 7 iterations}
    fitsX:=15;
    counter:=1; {never divide by zero}
    sd_old:=sd;
    while fitsX<=img_info.img_width-1-15 do
    begin
      fitsY:=15;
      while fitsY<=img_info.img_height-1-15 do
      begin
        value:=img[colour,fitsX,fitsY];
        if ((value<background*2) and (value<>0)) then {not an outlier, noise should be symmetrical so should be less then twice background}
        begin
          if ((iterations=0) or (abs(value-background)<=3*sd_old)) then {ignore outliers after first run}
          begin
            sd:=sd+sqr(value-background); {sd}
            inc(counter);{keep record of number of pixels processed}
          end;
        end;
        inc(fitsY,stepsize);;{skip pixels for speed}
      end;
      inc(fitsX,stepsize);{skip pixels for speed}
    end;
    sd:=sqrt(sd/counter); {standard deviation}
    inc(iterations);
  until (((sd_old-sd)<0.05*sd) or (iterations>=7));{repeat until sd is stable or 7 iterations}
  noise_level[colour]:= round(sd);   {this noise level is too high for long exposures and if no flat is applied. So for images where center is brighter then the corners.}
end;

procedure QuickSort(var A: array of double; iLo, iHi: Integer) ;{ Fast quick sort. Sorts elements in the array list with indices between lo and hi}
var
  Lo, Hi : integer;
  Pivot, T: double;{ pivot, T, T2 are the same type as the elements of array }
begin
  Lo := iLo;
  Hi := iHi;
  Pivot := A[(Lo + Hi) div 2];
  repeat
    while A[Lo] < Pivot do Inc(Lo) ;
    while A[Hi] > Pivot do Dec(Hi) ;
    if Lo <= Hi then
    begin {swap}
      T := A[Lo];
      A[Lo] := A[Hi];
      A[Hi] := T;
      Inc(Lo) ;
      Dec(Hi) ;
    end;
  until Lo > Hi;
  if Hi > iLo then QuickSort(A, iLo, Hi) ;  {executes itself recursively}
  if Lo < iHi then QuickSort(A, Lo, iHi) ;  {executes itself recursively}
end;

function SMedian(list: array of double; leng: integer): double;{get median of an array of double. Taken from CCDciel code but slightly modified}
var
  mid : integer;
begin
 if leng=0 then result:=nan
 else
   if leng=1 then result:=list[0]
   else
   begin
     quickSort(list,0,leng-1);
     mid := (leng-1) div 2; //(high(list) - low(list)) div 2;
     if Odd(leng) then
     begin
       if leng<=3 then  result:=list[mid]
       else
       begin
         result:=(list[mid-1]+list[mid]+list[mid+1])/3;
       end;
     end
     else
     result:=(list[mid]+list[mid+1])/2;
  end;
end;


procedure HFD(const img: image_array; const img_info: PImageInfo; x1,y1,rs {boxsize}: integer; out hfd1,star_fwhm,snr{peak/sigma noise}, flux,xc,yc:double);
const
  max_ri=74; //(50*sqrt(2)+1 assuming rs<=50. Should be larger or equal then sqrt(sqr(rs+rs)+sqr(rs+rs))+1+2;
var
  i,j,r1_square,r2_square,r2, distance,distance_top_value,illuminated_pixels,signal_counter,counter :integer;
  SumVal, SumValX,SumValY,SumValR, Xg,Yg, r, val,bg,pixel_counter,valmax,mad_bg,sd_bg    : double;
  HistStart,boxed : boolean;
  r_aperture, img_width, img_height: integer;
  distance_histogram : array [0..max_ri] of integer;
  background : array [0..1000] of double; {size =3*(2*PI()*(50+3)) assuming rs<=50}

    function value_subpixel(x1,y1:double):double; {calculate image pixel value on subpixel level}
    var
      x_trunc,y_trunc: integer;
      x_frac,y_frac  : double;
    begin
      x_trunc:=trunc(x1);
      y_trunc:=trunc(y1);
      if ((x_trunc<=0) or (x_trunc>=(img_width - 2)) or (y_trunc<=0) or (y_trunc>=(img_height - 2))) then begin result:=0; exit;end;
      x_frac :=frac(x1);
      y_frac :=frac(y1);
      try
        result:=         (img[0,x_trunc  ,y_trunc  ]) * (1-x_frac)*(1-y_frac);{pixel left top, 1}
        result:=result + (img[0,x_trunc+1,y_trunc  ]) * (  x_frac)*(1-y_frac);{pixel right top, 2}
        result:=result + (img[0,x_trunc  ,y_trunc+1]) * (1-x_frac)*(  y_frac);{pixel left bottom, 3}
        result:=result + (img[0,x_trunc+1,y_trunc+1]) * (  x_frac)*(  y_frac);{pixel right bottom, 4}
      except
      end;
    end;
begin
  {rs should be <=50 to prevent runtime errors}
  r1_square:=rs*rs;{square radius}
  r2:=rs+1;{annulus width us 1}
  r2_square:=r2*r2;
  img_width := img_info^.img_width;
  img_height := img_info^.img_height;

  if ((x1-r2<=0) or (x1 + r2 >= img_width - 1) or
      (y1-r2<=0) or (y1 + r2 >= img_height - 1) )
    then begin hfd1:=999; snr:=0; exit;end;

  valmax:=0;
  hfd1:=999;
  snr:=0;

  try
    counter:=0;
    for i:=-r2 to r2 do {calculate the mean outside the the detection area}
    for j:=-r2 to r2 do
    begin
      distance:=i*i+j*j; {working with sqr(distance) is faster then applying sqrt}
      if ((distance>r1_square) and (distance<=r2_square)) then {annulus, circular area outside rs, typical one pixel wide}
      begin
        background[counter]:=img[0,x1+i,y1+j];
        //for testing: mainwindow.image1.canvas.pixels[x1+i,y1+j]:=$AAAAAA;
        inc(counter);
      end;
    end;

    bg:=Smedian(background,counter);
    for i:=0 to counter-1 do background[i]:=abs(background[i] - bg);{fill background with offsets}
    mad_bg:=Smedian(background,counter); //median absolute deviation (MAD)
    sd_bg:=mad_bg*1.4826; {Conversion from mad to sd for a normal distribution. See https://en.wikipedia.org/wiki/Median_absolute_deviation}
    sd_bg:=max(sd_bg,1); {add some value for images with zero noise background. This will prevent that background is seen as a star. E.g. some jpg processed by nova.astrometry.net}
    {sd_bg and r_aperture are global variables}

    repeat {reduce square annulus radius till symmetry to remove stars}
    // Get center of gravity whithin star detection box and count signal pixels, repeat reduce annulus radius till symmetry to remove stars
      SumVal:=0;
      SumValX:=0;
      SumValY:=0;
      signal_counter:=0;

      for i:=-rs to rs do
      for j:=-rs to rs do
      begin
        val:=(img[0,x1+i,y1+j])- bg;
        if val>3.0*sd_bg then
        begin
          SumVal:=SumVal+val;
          SumValX:=SumValX+val*(i);
          SumValY:=SumValY+val*(j);
          inc(signal_counter); {how many pixels are illuminated}
        end;
      end;
      if sumval<= 12*sd_bg then
         exit; {no star found, too noisy, exit with hfd=999}

      Xg:=SumValX/SumVal;
      Yg:=SumValY/SumVal;
      xc:=(x1+Xg);
      yc:=(y1+Yg);
     {center of gravity found}

      if ((xc-rs<0) or (xc+rs> img_width - 1) or (yc-rs<0) or (yc+rs> img_height - 1) ) then
                                 exit;{prevent runtime errors near sides of images}
      boxed:=(signal_counter>=(2/9)*sqr(rs+rs+1));{are inside the box 2 of the 9 of the pixels illuminated? Works in general better for solving then ovality measurement as used in the past}

      if boxed=false then
      begin
        if rs>4 then dec(rs,2) else dec(rs,1); {try a smaller window to exclude nearby stars}
      end;

      {check on hot pixels}
      if signal_counter<=1  then
      exit; {one hot pixel}
    until ((boxed) or (rs<=1)) ;{loop and reduce aperture radius until star is boxed}

    inc(rs,2);{add some space}

    // Build signal histogram from center of gravity
    for i:=0 to rs do distance_histogram[i]:=0;{clear signal histogram for the range used}
    for i:=-rs to rs do begin
      for j:=-rs to rs do begin

        distance:=round(sqrt(i*i + j*j)); {distance from gravity center} {modA}
        if distance<=rs then {build histogram for circel with radius rs}
        begin
          val:=value_subpixel(xc+i,yc+j)-bg;
          if val>3.0*sd_bg then {3 * sd should be signal }
          begin
            distance_histogram[distance]:=distance_histogram[distance]+1;{build distance histogram up to circel with diameter rs}
            if val>valmax then valmax:=val;{record the peak value of the star}
          end;
        end;
      end;
    end;

    r_aperture:=-1;
    distance_top_value:=0;
    HistStart:=false;
    illuminated_pixels:=0;
    repeat
      inc(r_aperture);
      illuminated_pixels:=illuminated_pixels+distance_histogram[r_aperture];
      if distance_histogram[r_aperture]>0 then HistStart:=true;{continue until we found a value>0, center of defocused star image can be black having a central obstruction in the telescope}
      if distance_top_value<distance_histogram[r_aperture] then distance_top_value:=distance_histogram[r_aperture]; {this should be 2*pi*r_aperture if it is nice defocused star disk}
    until ( (r_aperture>=rs) or (HistStart and (distance_histogram[r_aperture]<=0.1*distance_top_value {drop-off detection})));{find a distance where there is no pixel illuminated, so the border of the star image of interest}
    if r_aperture>=rs then exit; {star is equal or larger then box, abort}

    if (r_aperture>2)and(illuminated_pixels<0.35*sqr(r_aperture+r_aperture-2)){35% surface} then exit;  {not a star disk but stars, abort with hfd 999}

    except
  end;

  // Get HFD
  SumVal:=0;
  SumValR:=0;
  pixel_counter:=0;


  // Get HFD using the aproximation routine assuming that HFD line divides the star in equal portions of gravity:
  for i:=-r_aperture to r_aperture do {Make steps of one pixel}
  for j:=-r_aperture to r_aperture do
  begin
    Val:=value_subpixel(xc+i,yc+j)-bg; {The calculated center of gravity is a floating point position and can be anyware, so calculate pixel values on sub-pixel level}
    r:=sqrt(i*i+j*j); {Distance from star gravity center}
    SumVal:=SumVal+Val;{Sumval will be star total star flux}
    SumValR:=SumValR+Val*r; {Method Kazuhisa Miyashita, see notes of HFD calculation method, note calculate HFD over square area. Works more accurate then for round area}
    if val>=valmax*0.5 then pixel_counter:=pixel_counter+1;{How many pixels are above half maximum}
  end;
  flux:=max(sumval,0.00001);{prevent dividing by zero or negative values}
  hfd1:=2*SumValR/flux;
  hfd1:=max(0.7,hfd1);

  star_fwhm:=2*sqrt(pixel_counter/pi);{calculate from surface (by counting pixels above half max) the diameter equals FWHM }

  snr:=flux/sqrt(flux +sqr(r_aperture)*pi*sqr(sd_bg));
    {For both bright stars (shot-noise limited) or skybackground limited situations
    snr := signal/noise
    snr := star_signal/sqrt(total_signal)
    snr := star_signal/sqrt(star_signal + sky_signal)
    equals
    snr:=flux/sqrt(flux + r*r*pi* sd^2).

    r is the diameter used for star flux measurement. Flux is the total star flux detected above 3* sd.

    Assuming unity gain ADU/e-=1
    See https://en.wikipedia.org/wiki/Signal-to-noise_ratio_(imaging)
    https://www1.phys.vt.edu/~jhs/phys3154/snr20040108.pdf
    http://spiff.rit.edu/classes/phys373/lectures/signal/signal_illus.html}


  {==========Notes on HFD calculation method=================
    Documented this HFD definition also in https://en.wikipedia.org/wiki/Half_flux_diameter
    References:
    https://astro-limovie.info/occultation_observation/halffluxdiameter/halffluxdiameter_en.html       by Kazuhisa Miyashita. No sub-pixel calculation
    https://www.lost-infinity.com/night-sky-image-processing-part-6-measuring-the-half-flux-diameter-hfd-of-a-star-a-simple-c-implementation/
    http://www.ccdware.com/Files/ITS%20Paper.pdf     See page 10, HFD Measurement Algorithm

    HFD, Half Flux Diameter is defined as: The diameter of circle where total flux value of pixels inside is equal to the outside pixel's.
    HFR, half flux radius:=0.5*HFD
    The pixel_flux:=pixel_value - background.

    The approximation routine assumes that the HFD line divides the star in equal portions of gravity:
        sum(pixel_flux * (distance_from_the_centroid - HFR))=0
    This can be rewritten as
       sum(pixel_flux * distance_from_the_centroid) - sum(pixel_values * (HFR))=0
       or
       HFR:=sum(pixel_flux * distance_from_the_centroid))/sum(pixel_flux)
       HFD:=2*HFR

    This is not an exact method but a very efficient routine. Numerical checking with an a highly oversampled artificial Gaussian shaped star indicates the following:

    Perfect two dimensional Gaussian shape with σ=1:   Numerical HFD=2.3548*σ                     Approximation 2.5066, an offset of +6.4%
    Homogeneous disk of a single value  :              Numerical HFD:=disk_diameter/sqrt(2)       Approximation disk_diameter/1.5, an offset of -6.1%

    The approximate routine is robust and efficient.

    Since the number of pixels illuminated is small and the calculated center of star gravity is not at the center of an pixel, above summation should be calculated on sub-pixel level (as used here)
    or the image should be re-sampled to a higher resolution.

    A sufficient signal to noise is required to have valid HFD value due to background noise.

    Note that for perfect Gaussian shape both the HFD and FWHM are at the same 2.3548 σ.
    }


   {=============Notes on FWHM:=====================
      1)	Determine the background level by the averaging the boarder pixels.
      2)	Calculate the standard deviation of the background.

          Signal is anything 3 * standard deviation above background

      3)	Determine the maximum signal level of region of interest.
      4)	Count pixels which are equal or above half maximum level.
      5)	Use the pixel count as area and calculate the diameter of that area  as diameter:=2 *sqrt(count/pi).}
end;

{* find background, number of stars, median HFD *}
function analyse_image(
  const img: image_array;
  const img_info: TImageInfo;
  snr_min: double;
  max_stars: integer;
  out hfd_median, fwhm_median, background : double): integer;
const
  MAX_RETRIES : integer = 2;
  BOX_SIZE: integer = 25;
var
  img_sa                      : TBitMatrix;
  noise_level                 : colored_stat_array;
  star_level, detection_level : double;
  worker_context              : TImgAnalyseContext;
  worker_threads              : array[1..8] of TImgAnalyseThread;
  hfd_list, fwhm_list         : array of double;

  y_start, y_end, retries, star_counter, i, j      : integer;
  worker_range, fitsY, len, new_star_counter, temp : integer;

  function ShouldSuspend(aIdx: integer): Boolean;
  begin
    Result := ((aIdx - low(worker_threads)) mod 2) = 0;
  end;
begin
  if max_stars <= 0 then
    max_stars := 500;

  if snr_min <= 0 then
    snr_min := 10;

  worker_range := round(img_info.img_height / high(worker_threads));
  get_background(0, img, img_info, background, star_level, noise_level);

  worker_context.box_size     := BOX_SIZE;
  worker_context.max_stars    := max_stars;
  worker_context.snr_min      := snr_min;
  worker_context.background   := background;
  worker_context.thread_count := high(worker_threads);

  WriteLn('bs: ', worker_context.box_size, ' max_stars: ', worker_context.max_stars, ' snr_min: ', trunc(snr_min), ' bkg: ', trunc(background));

  detection_level:=max(3.5 * noise_level[0], star_level); {level above background. Start with a high value}
  retries := MAX_RETRIES; {try up to three times to get enough stars from the image}

  if ((background < 60000) and (background > 8)) then {not an abnormal file}
  begin
    SetLength(img_sa, img_info.img_height); {set length of array to image height}
    len := max_stars;
    SetLength(hfd_list, len);
    SetLength(fwhm_list, len);

    for fitsY := 0 to img_info.img_height - 1 do
      img_sa[fitsY] := TBits.Create(img_info.img_width);

    repeat {try three time to find enough stars}
      star_counter := 0;

      WriteLn('thread count: ', worker_context.thread_count, ' worker_range: ', worker_range, ' star level: ', star_level, ' noise level: ', 3.5 * noise_level[0], ' detection level: ', detection_level);

      if retries < MAX_RETRIES then
        for fitsY := 0 to img_info.img_height - 1 do
          img_sa[fitsY].Clearall; {mark row as star free unsurveyed area}

      { init threads }
      y_start := 0;
      for I := low(worker_threads) to high(worker_threads) do
      begin
        if I = high(worker_threads) then
          y_end := img_info.img_height
        else
          y_end := y_start + worker_range;
        worker_threads[I] := TImgAnalyseThread.Create(img, img_sa, @img_info, @worker_context, y_start, y_end, detection_level, ShouldSuspend(I));

        y_start := y_end;
      end;

      { wait for all "odd" threads to finish and start the "even" threads }
      for I := low(worker_threads) to high(worker_threads) do
        if not ShouldSuspend(I) then
        begin
          worker_threads[I].WaitFor;
          { start the thread before }
          worker_threads[I - 1].Start
        end;

      { wait for the "even" threads to finish }
      for I := low(worker_threads) to high(worker_threads) do
      begin
        if ShouldSuspend(I) then
          worker_threads[I].WaitFor;

        temp := worker_threads[I].StarCounter;
        new_star_counter := star_counter + temp;

        if new_star_counter >= len then
        begin
          len := Max(len + max_stars, new_star_counter);
          SetLength(hfd_list, len);
          SetLength(fwhm_list, len)
        end;

        // WriteLn('Copy ', worker_threads[I].StarCounter, ' to local lists #', star_counter, ' first star: hfd=', worker_threads[I].HFDList[0], ' fwhm=', worker_threads[I].FWHMList[0]);

        for J := 0 to temp - 1 do
        begin
          hfd_list[star_counter + J] := worker_threads[I].HFDList[J];
          fwhm_list[star_counter + J] := worker_threads[I].FWHMList[J];
        end;

        star_counter := new_star_counter;
        worker_threads[I].Free;
      end;

      { after execution }
      dec(retries);

      { In principle not required. Try again with lower detection level }
      if detection_level <= 7 * noise_level[0] then
        retries := -1 {stop}
      else
        detection_level:=max(6.999 * noise_level[0], min(30 * noise_level[0], detection_level * 6.999 / 30)); {very high -> 30 -> 7 -> stop.  Or  60 -> 14 -> 7.0. Or for very short exposures 3.5 -> stop}

    until ((star_counter >= max_stars) or (retries < 0)); {reduce detection level till enough stars are found. Note that faint stars have less positional accuracy}

    if star_counter > 0 then
    begin
      hfd_median  := SMedian(hfd_list, star_counter);
      fwhm_median := SMedian(fwhm_list, star_counter);
    end
    else
    begin
      hfd_median  := 99;
      fwhm_median := 99
    end;

    Result := star_counter;

    { free mem }
    hfd_list  := nil;
    fwhm_list := nil;

    {free mem of star area}
    for fitsY := 0 to img_info.img_height - 1 do
      img_sa[fitsY].Free;

    img_sa := nil;

  end {background is normal}
  else
  begin
    hfd_median  := 99; {Most common value image is too low. Cannot process this image. Check camera offset setting.}
    fwhm_median := 99;
    Result      := -1;
  end;
end;

end.

