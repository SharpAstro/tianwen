unit analysis;

{$mode ObjFPC}{$H+}

interface

type
  image_array = array of array of array of double;
  histogram_array = array[0..2,0..65535] of integer;{red,green,blue,count}
  colored_stat_array = array[0..2] of double;

  HistogramStats = packed record
    red: integer;
    green: integer;
    blue: integer;
    mean : colored_stat_array;
  end;

  ImageInfo = packed record
    img_width: integer;
    img_height: integer;
    bit_depth: integer;
  end;


  { get histogram of each colour, and their mean and total values }
  function get_hist(colour:integer; img :image_array; const img_info: ImageInfo; out histogram_stats: HistogramStats) : histogram_array;

  { get background and star level from peek histogram }
  procedure get_background(colour: integer; img :image_array; const img_info: ImageInfo; out background, starlevel: double; out noise_level: colored_stat_array);

  { Fast quick sort. Sorts elements in the array list with indices between lo and hi }
  procedure QuickSort(var A: array of double; iLo, iHi: Integer);

  { get median of an array of double. Taken from CCDciel code but slightly modified }
  function SMedian(list: array of double; leng: integer): double;

  { find background, number of stars, median HFD, returns star count }
  function analyse_image(img: image_array; img_info: ImageInfo; snr_min: double; max_stars: integer; out hfd_median, fwhm_median, background : double): integer;

implementation

uses
  Math;


function get_hist(colour:integer; img :image_array; const img_info: ImageInfo; out histogram_stats: HistogramStats) : histogram_array;
var
     i,j,col,his_total,count,offsetW,offsetH : integer;
     total_value                             : double;
begin
  if colour+1>length(img) then {robust detection, case binning is applied and image is mono}
    colour:=0; {used red only}

  for i:=0 to 65535 do
    Result[colour,i] := 0;{clear histogram of specified colour}

  his_total:=0;
  total_value:=0;
  count:=1;{prevent divide by zero}

  offsetW:=trunc(img_info.img_width * 0.042); {if Libraw is used, ignored unused sensor areas up to 4.2%}
  offsetH:=trunc(img_info.img_height * 0.015); {if Libraw is used, ignored unused sensor areas up to 1.5%}


  For i:=0+offsetH to img_info.img_height - 1 - offsetH do
  begin
    for j:=0+offsetW to img_info.img_width - 1 - offsetW do
    begin
      col:=round(img[colour,j,i]);{red}
      if ((col>=1) and (col<65000)) then {ignore black overlap areas and bright stars}
      begin
        inc(Result[colour,col],1);{calculate histogram}
        his_total:=his_total+1;
        total_value:=total_value+col;
        inc(count);
      end;
    end;{j}
  end; {i}

  if colour=0 then histogram_stats.red := his_total
  else
  if colour=1 then histogram_stats.green := his_total
  else
  histogram_stats.blue := his_total;

  histogram_stats.mean[colour] := total_value/count;

end;


procedure get_background(colour: integer; img :image_array; const img_info: ImageInfo; out background, starlevel: double; out noise_level: colored_stat_array); {get background and star level from peek histogram}
var
  i, pixels,max_range,above,his_total, fitsX, fitsY,counter,stepsize, iterations : integer;
  value,sd, sd_old : double;
  histogram_stats : HistogramStats;
  histogram : histogram_array;
begin
  histogram := get_hist(colour, img, img_info, histogram_stats);{get histogram of img_loaded and his_total}

  background:=img[0,0,0];{define something for images containing 0 or 65535 only}

  {find peak in histogram which should be the average background}
  pixels:=0;
  max_range := round(histogram_stats.mean[colour]); {mean value from histogram}
  for i := 1 to max_range do {find peak, ignore value 0 from oversize}
    if histogram[colour,i]>pixels then {find colour peak}
    begin
      pixels:= histogram[colour,i];
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
     above:=above+histogram[colour,i];
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


procedure HFD(img: image_array; const img_info: ImageInfo; x1,y1,rs {boxsize}: integer; out hfd1,star_fwhm,snr{peak/sigma noise}, flux,xc,yc:double);{calculate star HFD and FWHM, SNR, xc and yc are center of gravity. All x,y coordinates in array[0..] positions}
const
  max_ri=74; //(50*sqrt(2)+1 assuming rs<=50. Should be larger or equal then sqrt(sqr(rs+rs)+sqr(rs+rs))+1+2;
var
  i,j,r1_square,r2_square,r2, distance,distance_top_value,illuminated_pixels,signal_counter,counter :integer;
  SumVal, SumValX,SumValY,SumValR, Xg,Yg, r, val,bg,pixel_counter,valmax,mad_bg,sd_bg    : double;
  HistStart,boxed : boolean;
  r_aperture: integer;
  distance_histogram : array [0..max_ri] of integer;
  background : array [0..1000] of double; {size =3*(2*PI()*(50+3)) assuming rs<=50}

    function value_subpixel(x1,y1:double):double; {calculate image pixel value on subpixel level}
    var
      x_trunc,y_trunc: integer;
      x_frac,y_frac  : double;
    begin
      x_trunc:=trunc(x1);
      y_trunc:=trunc(y1);
      if ((x_trunc<=0) or (x_trunc>=(img_info.img_width - 2)) or (y_trunc<=0) or (y_trunc>=(img_info.img_height - 2))) then begin result:=0; exit;end;
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

  if ((x1-r2<=0) or (x1+r2>=img_info.img_width - 1) or
      (y1-r2<=0) or (y1+r2>=img_info.img_height - 1) )
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

      if ((xc-rs<0) or (xc+rs> img_info.img_width - 1) or (yc-rs<0) or (yc+rs> img_info.img_height - 1) ) then
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
  img: image_array;
  img_info: ImageInfo;
  snr_min: double;
  max_stars: integer;
  out hfd_median, fwhm_median, background : double): integer;
var
  i, j, len, retries, star_counter                    : integer;
  fitsX, fitsY, diam, m, n, xci, yci, sqr_diam        : integer;
  hfd1, star_fwhm, snr, flux, xc, yc, detection_level : double;
  hfd_list, fwhm_list                                 : array of double;
  img_sa                                              : image_array;
  noise_level                                         : colored_stat_array;
  star_level                                          : double;
begin
  if max_stars <= 0 then
    max_stars := 500;

  if snr_min <= 0 then
    snr_min := 10;

  len := max_stars; { start list length with star target }
  SetLength(hfd_list, len);
  SetLength(fwhm_list, len);

  get_background(0, img, img_info, background, star_level, noise_level);

  detection_level:=max(3.5 * noise_level[0], star_level); {level above background. Start with a high value}
  retries:=2; {try up to three times to get enough stars from the image}

  if ((background < 60000) and (background > 8)) then {not an abnormal file}
  begin
    SetLength(img_sa, 1, img_info.img_width, img_info.img_height); {set length of image array}
    repeat {try three time to find enough stars}
      star_counter:=0;

      for fitsY:=0 to img_info.img_height - 1 do
        for fitsX:=0 to img_info.img_width - 1  do
          img_sa[0, fitsX, fitsY] := 0; {mark as star free unsurveyed area}

      for fitsY:=0 to img_info.img_height - 1 do
      begin
        for fitsX:=0 to img_info.img_width - 1  do
        begin
          if ((img_sa[0, fitsX, fitsY] <= 0){star free area} and (img[0, fitsX, fitsY] - background > detection_level)) then {new star. For analyse used sigma is 5, so not too low.}
          begin
            HFD(img, img_info, fitsX, fitsY, 14{box size}, hfd1, star_fwhm, snr, flux, xc, yc);{star HFD and FWHM}
            if ((hfd1<=30) and (snr>snr_min) and (hfd1>0.8) {two pixels minimum} ) then
            begin
              hfd_list[star_counter]  := hfd1;
              fwhm_list[star_counter] := star_fwhm;

              inc(star_counter);
              if star_counter >= len then
              begin
                len := len + max_stars;
                SetLength(hfd_list, len);
                SetLength(fwhm_list, len)
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
                  if ((j>=0) and (i>=0) and (j< img_info.img_height) and (i < img_info.img_width) and ( (sqr(m)+sqr(n)) <= sqr_diam)) then
                    img_sa[0,i,j]:=1;
                end;
            end;
          end;
        end;
      end;

      dec(retries); {In principle not required. Try again with lower detection level}
      if detection_level <= 7 * noise_level[0] then
        retries:= -1 {stop}
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
  end {background is normal}
  else
  begin
    hfd_median  := 99; {Most common value image is too low. Cannot process this image. Check camera offset setting.}
    fwhm_median := 99;
    Result      := -1;
  end;

  hfd_list  := nil;
  fwhm_list := nil;
  img_sa    := nil; {free mem}
end;

end.

