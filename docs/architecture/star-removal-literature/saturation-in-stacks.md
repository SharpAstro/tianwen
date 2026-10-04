# Literature review B: how a star saturates, in the sensor and in a stack

> One of the reviews behind [docs/architecture/star-removal-literature.md](../star-removal-literature.md), made for [docs/plans/star-remover-training.md](../../plans/star-remover-training.md) on 2026-10-03. Its "we" and "our" are TianWen; R0, the injector and the measurements quoted are the plan's as they stood that day, including the ten-master measurements of 2026-10-03 listed in section 0.

The question this review answers: what happens to the top of a star between the photosite and the stacked master, which
of those steps the injector's render (eight virtual subs, each clipped hard, then averaged) leaves out, and what the
literature says about the halo excess of barely saturated stars.

## 0. How to read this

**Verified** means I fetched the primary page on 2026-10-03: the publisher or Crossref record of the DOI, the arXiv PDF,
the ADS full-text scan, the manufacturer's datasheet, manual or flyer, the software's own documentation, its source file,
or a forum post by the software's author. Each entry says how far I read:

- **full text read**: the whole paper or document;
- **method section read**: the sections named in the entry;
- **abstract read**: the abstract and bibliographic record only;
- **documentation read**: the manual, flyer, documentation page or forum post (for a camera manual, the gain chart
  images too, which I extracted from the PDF and read);
- **source read**: the named source file;
- **metadata only**: title, authors and venue confirmed, nothing of the content.

Nothing is cited that I could not reach. Leads I could not verify, or verified only in part, are in section 7 and are
not used in the body. Numbers marked **(my arithmetic)** are mine, not the source's; the derivations are in Appendix A
and the assumptions behind them in Appendix B.

Our context, as given:

- **Data.** Amateur stacks; cameras ZWO ASI533MC, ASI585MC, ASI294MM, ASI1600MM (Panasonic MN34230, 12-bit), QHY294C,
  QHY183M, SVBONY SV605CC, Player One Uranus-C (IMX585); gains from 0 to above unity; 16-bit FITS; darks, flats, bias;
  registration by clamped Lanczos-3; sigma-clip rejection; per-sub normalisation (scale and offset); OSC by AHD demosaic or
  Bayer drizzle; 20 to 300 subs.
- **The injector's render.** A saturated star is eight virtual subs, each with its own amplitude and width (5 percent
  lognormal scatter each) and sub-pixel offset (0.25 px RMS per axis), each clipped hard where plate plus star passes the
  channel's clip level (the master's own core maximum for the donor star), then averaged; noise added after, scaled by
  the fraction of subs that did not clip.
- **What the masters show.** Saturated tops are rounded: median plateau 1 px (pixels within 2 percent of the max), first
  ring 0.79 to 0.90 of the max; the core maximum varies from star to star (Leo Triplet, ASI294MM: 0.84 to 0.93 of full
  scale); on 18 to 24 stars of some masters one colour channel clips first. A clipped sub warped by clamped Lanczos-3
  keeps its first ring at 0.995 of the max, so the warp does not round a top.
- **New on 2026-10-03, ten masters.** The too-wide injected tops were mostly not a saturation problem: the saturated
  amplitude was fitted with R0's own profile (field Moffat plus a radial residual table) but rendered with a wingless
  Moffat, which made the core 1.2 to 1.6 times too bright. Rendered with R0's profile, the eight hard-clipped virtual subs
  reproduce the real tops (first ring within 0.01 to 0.04 of real on six masters, 0.09 to 0.11 on two; plateau 1 px like
  real). Two exceptions: the ASI1600MM master (12-bit; the real top is a few percent below flat over 3 px where the render
  is flat over 6) and a 24 mm lens master (plateau 3 px rendered against 2 px real). And saturated stars hold more halo
  than R0's field profile predicts (real over field 1.1 to 1.6 at 2.5 to 4 px), an excess that shrinks as the star is
  driven further past its clip (Spearman -0.4 to -0.6 on five masters). Two hypotheses are on the table: **H1**, a barely
  saturated star clips in only some subs, so its near-core pixels are softly compressed in the stack and the wing-fitted
  amplitude comes out low; **H2**, the sensor itself is nonlinear near full well.

## 1. The short version

1. **At most of our gains each sub clips hard, at the ADC maximum, so a hard-clip render is the right base.** Every ZWO
   gain chart I read (ASI1600, ASI294MM in both modes, ASI533, ASI585) gives a "full well" that is the ADC range in
   electrons: e-/ADU at gain 0 times 2^bits equals the stated full well to within 2.5 percent, about what a chart can be
   read to (20,480 e, 66,355 e, 14,418 e, 50,790 e, 47,104 e against 20k, 66.4k, 14.4k, 50k, 47k) **(my arithmetic)**.
   So the well and the ADC run out
   together only at gain 0; at any higher gain the ADC clips first, at a fraction of the well (a fifth on the ASI1600 at
   unity). EMVA 1288 expects exactly this ("the signal is clipped to the maximum digital value 2^k - 1 before the physical
   saturation of the pixel is reached"), and Alarcon et al. (2023) measured a Sony back-illuminated sensor linear to within
   2 percent up to a saturation level of 65532 ADU. This is why the hard-clipped render reproduces the real tops once its
   profile is right. **Prediction:** in raw subs, saturated cores sit exactly at 65520 (12-bit) or 65532 (14-bit) at every
   gain above about 0; a spread of core values below that, beyond noise, marks a non-digital limit.
2. **The top of a strongly overexposed star cannot tell us the scatter; a barely saturated star can.** For a star whose true
   signal at 1 px is twice the clip, no plausible scatter of clip level, width or position lowers the first ring: a sub
   would need its width halved or its position moved about 1 px **(my arithmetic, A.4)**. That is why 5 percent scatter
   suffices for the tops, and why the tops cannot calibrate the scatter. The scatter shows only where the true signal is
   within the spread of the per-sub clip levels, that is in barely saturated stars, which is where the halo excess lives.
3. **Calibration and normalisation turn one clip level into many.** A calibrated pixel clips at (ADC maximum minus dark)
   divided by the flat: "after flat-fielding the images, there is a large variation in the effective saturation level"
   (Dohm-Palmer et al. 2000). Per-sub normalisation then moves each sub's clip by its own offset and scale; SWarp's guide
   states the consequence for any co-add (the output saturation level "is defined as the minimum of all input saturation
   values after astrometric and photometric rescaling"), and MUSYC found its stacks' empirical saturation level "usually a
   factor of a few less than the apparent saturation level of the brightest stars". Averaging hard clips at spread levels
   gives a soft knee as wide as the spread **(my arithmetic, A.3)**. **Prediction:** across one master's strongly
   overexposed stars the core maximum follows 1/flat (slope near -1 in log), which can account for Leo's 11 percent spread
   (0.84 to 0.93); the knee width follows the spread of the stacker's per-sub scales and offsets.
4. **Seeing varies by tens of percent within a session, and a star's peak varies twice as much.** Racine (1996) found
   free-atmosphere seeing log-normal with a dispersion of 0.175 dex (a seeing ratio of 1.50), decorrelating with an
   e-folding time of 17 minutes. At fixed flux a star's peak goes as FWHM^-2, so a 20 percent FWHM scatter is a 40 percent
   peak scatter. Pan-STARRS found that "pixels in the wings of bright stars are liable to be over-rejected as the image
   quality changes" and convolves its inputs to a common PSF before rejecting. Our render's 5 percent width scatter is
   probably low by a factor 2 to 6 (to be measured from our frame metrics).
5. **The halo excess fits H1 in size and in sign; H2 is possible only at gain near 0 or on IMX294/IMX492 in HCG.** A star
   whose median sub just reaches the clip is clipped in its sharp subs and not its soft ones, so its stacked core is
   depleted by 9, 19 or 31 percent for a FWHM scatter of 10, 20 or 30 percent; an amplitude read off those core pixels then
   makes real over field 1.10, 1.24 or 1.45 at radii the clip never reaches **(my arithmetic, A.5)**, the observed 1.1 to
   1.6. The depletion falls as overexposure pushes the knee out toward the radius where a star's intensity at fixed flux
   does not depend on seeing (alpha/sqrt(beta - 1), 2.1 px for FWHM 3 px and beta 3; A.6), which gives the negative
   Spearman. The brighter-fatter effect (Bosch et al. 2018, in CCDs) predicts the opposite sign. H2 has literature behind
   it (CMOS nonlinearity of "several percent", Wang and Theuwissen 2017; neighbour-dependent saturation and blooming, Chao et
   al. 2013; an IMX294 that "does not fully saturate" in HCG below about 8x gain, Glover 2022), but the gain charts confine
   it to subs where the well, not the ADC, is the limit. **Decisive test:** the breakpoint of peak against aperture flux
   (the method of Revalski et al. 2025) in single raw subs. A sharp break at the ADC maximum means H1; a bend below it, at
   a fixed raw level, means H2. Confirm by restacking with FWHM-matched subs (H1 shrinks, H2 does not) and by splitting the
   masters by their GAIN card (H2 depends on it, H1 does not).
6. **The two exceptions point to more spread, or to the sensor, and each has a cheap test.** The ASI1600MM master (droop of
   a few percent over 3 px where the render is flat over 6): with a per-sub clip spread of 30 percent instead of 5, the
   2 percent plateau needs the true signal at 1.145 times the mean clip instead of 0.987 **(my arithmetic, A.3)**, which
   on a big overexposed star is a much smaller radius. A 12-bit clip at unity gain is only 4095 e, so a changing sky
   offset is a large fraction of it. If its GAIN card is 0 instead, the well limits and the sensor can dome the top
   (2.10, 2.11). The 24 mm lens master (plateau 3 px against 2): a wide field spans a large range of airmass, so one global
   scale per sub cannot equalise transparency everywhere and the clip spread varies across the field; and an OSC demosaic
   or drizzle drop smooths a 2 to 3 px plateau (4.6).
7. **What the injector should change, only as the tests justify it.** Draw each virtual sub's clip from the session's own
   normalisation record and the master flat at the site, its width from the session's FWHM spread, use more subs than
   eight where the knee matters, add each sub's noise before clipping, and take the clip level from strongly overexposed
   donors, never a barely saturated donor's own maximum (section 6).

## 2. The sensor: where a sub clips, and how sharply

### 2.1 EMVA 2021, Standard 1288 Release 4.0 Linear: saturation capacity versus full well

- **Citation:** European Machine Vision Association, "EMVA Standard 1288: Standard for Characterization of Image Sensors
  and Cameras", Release 4.0 Linear, 16 June 2021.
- **Verified:** documentation read (sections 2.7, 6.5, 6.6 on saturation, 6.9 on linearity, appendices D and E).
  https://www.emva.org/wp-content/uploads/EMVA1288Linear_4.0Release.pdf
- **Code / licence:** the document is CC BY-ND 4.0.
- **What:** the usable maximum is below 2^k - 1 "because of the temporal noise and the photo response nonuniformity".
  "The saturation capacity must not be confused with the full-well capacity. It is normally lower than the full-well
  capacity, because the signal is clipped to the maximum digital value 2^k - 1 before the physical saturation of the pixel
  is reached." The saturation grey value is set where "between 0.1 - 0.2% of the total number of pixels show the maximum
  value". The linearity error is the mean relative deviation from a regression between 5 and 95 percent of the saturation
  capacity. Release 4.0 dropped the variance maximum of the photon transfer curve as the definition of saturation, because
  in some cases it "does not lead to a correct estimate".
- **Relevance to us:** the standard's normal case is the digital clip: a hard top per sub, softened only by noise and
  PRNU. And no EMVA datasheet says anything about the top 5 percent of the range.
- **Prediction:** in our raw subs at gains above about 0, saturated core pixels sit at the ADC maximum, with no tail below
  it beyond the shot noise of the pixels just short of it.

### 2.2 Janesick 2007, Photon Transfer

- **Citation:** J. R. Janesick, "Photon Transfer: DN to lambda", SPIE Press monograph PM170 (2007). DOI 10.1117/3.725073.
- **Verified:** metadata only (Crossref record, SPIE page). I have not read it.
- **What:** the photon transfer method that EMVA 1288 and Alarcon et al. (2.9) use to measure gain, read noise and full
  well.
- **Relevance to us:** the reference for the method; what it says about CMOS nonlinearity near full well is a lead
  (section 7).
- **Prediction:** none.

### 2.3 ZWO 2018, ASI1600 manual (Panasonic MN34230)

- **Citation:** ZWO, "ASI1600 Manual", Revision 1.3, March 2018.
- **Verified:** documentation read, including the figure "Read noise, full well, gain and dynamic range for ASI1600".
  https://astronomy-imaging-camera.com/manuals/ASI1600%20Manual%20EN.pdf
- **What:** MN34230ALJ (mono) and PLJ (colour); 12-bit ADC (10-bit in fast mode); full well 20 ke; read noise 3.6 e at
  gain 0 falling to 1.2 e at 30 dB. The chart gives about 5.0 e-/ADU at gain 0, unity gain at 139, and a "FW" curve that
  falls from 20k at gain 0 to under 1k at gain 300.
- **Relevance to us:** 5.0 x 4096 = 20,480 e **(my arithmetic)**: the charted "full well" at each gain is the ADC range in
  electrons, and equals the sensor's well only at gain 0. Above gain 0 the 12-bit ADC clips first, at 4095 (65520 in a
  16-bit file). At unity the clip is 4095 e, a fifth of the well, with shot noise 1.6 percent of the clip.
- **Prediction:** if the ASI1600MM master's subs were taken well above gain 0, its droop of a few percent cannot come from
  the sensor and must come from calibration or stacking (sections 3 and 4). If its GAIN card is 0 or near it, the well
  limits and a soft, pixel-dependent top is possible (2.10, 2.11).

### 2.4 ZWO 2022, ASI294 manual (Sony IMX294 colour, IMX492 mono)

- **Citation:** ZWO, "ASI294 Manual", Revision 2.2, February 2022.
- **Verified:** documentation read, including the three gain charts (ASI294MC, ASI294MM, ASI294MM unlocked bin 1).
  https://i.zwoastro.com/zwo-website/manuals/ASI294_Manual_EN_V2.2.pdf
- **What:** ASI294MM uses the IMX492, ASI294MC the IMX294. Bin 2 (4.63 um, 14-bit): full well 66.4 ke mono, 63.7 ke
  colour. Unlocked bin 1 (2.3 um, 47 MP, mono only, at most 12-bit): full well 14.417 ke. "When the gain is 120, the HCG
  mode will be automatically turned on"; read noise falls there from about 6 e to about 1.8 e. The charts give about
  4.05 e-/ADU at gain 0 in bin 2 and about 3.5 in bin 1.
- **Relevance to us:** 4.05 x 16384 = 66,355 e and 3.52 x 4096 = 14,418 e **(my arithmetic)**: the same pattern, the
  charted full well is the ADC range. Four times the bin 1 range (57.7k) is less than the bin 2 range (66.4k), so the bin 2
  range is not simply four bin 1 wells; whether the four photosites behind a bin 2 pixel can saturate separately before
  the sum reaches the ADC maximum is not stated (2.7).
- **Prediction:** on our ASI294MM and QHY294C masters, flatness of the top and the core maximum depend on gain: hard and
  at full scale above about gain 60 (the ADC range there is at most half the well, A.1) except possibly in HCG between
  120 and about 190 (2.8); possibly soft at gain 0. Split the IMX294/IMX492 masters by their GAIN card.

### 2.5 ZWO 2021 and 2022, ASI533 and ASI585 manuals

- **Citation:** ZWO, "ASI533 Manual", Revision 1.2, August 2021; ZWO, "ASI585 Manual", Revision 1.0, August 2022.
- **Verified:** documentation read, including both gain charts.
  https://i.zwoastro.com/zwo-website/manuals/ASI533_Manual_EN_V1.2.pdf and
  https://i.zwoastro.com/zwo-website/manuals/ASI585_Manual_EN_V1.0.pdf
- **What:** ASI533 (IMX533): 14-bit, full well 50 ke, about 3.1 e-/ADU at gain 0, unity at gain 100 where the read noise
  steps down (HCG). ASI585 (IMX585): 12-bit, full well 47 ke in this revision, about 11.5 e-/ADU at gain 0, unity at
  gain 210, HCG at 252.
- **Relevance to us:** 3.1 x 16384 = 50.8k and 11.5 x 4096 = 47.1k **(my arithmetic)**: the well is the ADC range only at
  gain 0. The 12-bit IMX585 (our ASI585MC, and the Player One Uranus-C, whose own data I did not verify) clips at 4095 e
  at unity gain, 9 percent of the well. The SVBONY SV605CC is an IMX533 camera by its listing; I did not verify its data.
- **Prediction:** masters from these cameras at unity or HCG gains should show the hard-clip tops the render already
  reproduces; any softness there is made by calibration or stacking.

### 2.6 QHYCCD, QHY183 product page

- **Citation:** QHYCCD, "QHY183M and QHY183C" product page.
- **Verified:** documentation read (the specification list; the gain charts on the page are images I did not read).
  https://www.qhyccd.com/astronomical-camera-qhy183/
- **What:** Sony IMX183, 2.4 um, full well 15.5 ke-, "AD Sample Depth: 12bit (output as 16bit and 8bit)", read noise
  2.7 e- at the lowest gain and 1.0 e- at high gain, unity gain at 10.
- **Relevance to us:** a 12-bit clip like the ASI1600 and the IMX585: at unity it is 4095 e, about a quarter of the well
  **(my arithmetic)**. Our QHY183M eta Carinae master (288 mm, SII, a 67 px plateau) is such a low-electron clip.
- **Prediction:** as 2.5.

### 2.7 Sony, IMX294CJK flyer: the Quad Bayer pixel is four photosites

- **Citation:** Sony Semiconductor Solutions, "IMX294CJK: Diagonal 21.63 mm (Type 4/3) Approx. 10.71M-Effective Pixel
  Color CMOS Image Sensor", product flyer (undated).
- **Verified:** documentation read. https://www.sony-semicon.com/files/62/flyer_security/IMX294CJK_Flyer.pdf
- **What:** "The IMX294CJK uses a Quad Bayer structure, and outputs data binned in 2 x 2 pixel units in normal mode"; the
  figure shows each output pixel as "1+2+3+4" of four physical pixels; unit cell 4.63 um; ADC selectable at 10, 12 or 14
  bits; a minimum saturation signal of 970 mV is listed (LCG; the table was partly garbled in my extraction).
- **Relevance to us:** a 4.63 um pixel of the IMX294 (QHY294C) is four photosites, and ZWO's unlocked 47 MP mode suggests
  the IMX492 (ASI294MM) is built the same way (my inference, not stated). The flyer does not say whether the four are summed
  as charge, as voltage or digitally. If each photosite has its own well and the sum is formed after they fill one by one,
  a pixel on a steep star core responds as a sum of four separately clipped values, a soft knee inside one pixel whose
  width follows the light gradient across it (my reasoning).
- **Prediction:** if it happens, only where a photosite's well fills before the ADC clips the sum, which is low gain; and
  most strongly for undersampled stars. In raw IMX294/IMX492 subs at gain 0, peak against aperture flux (2.13) bends
  before the ADC maximum; at gain 120 it does not.

### 2.8 Glover 2022, SharpCap forum: the IMX294 does not saturate in HCG below about 8x gain

- **Citation:** R. Glover (SharpCap's author, posting as admin), replies of 26 and 29 June 2022 in "A different flat
  option for IMX294 based camera owners", SharpCap Forums.
- **Verified:** documentation read (the forum thread). https://forums.sharpcap.co.uk/viewtopic.php?t=5561
- **What:** in HCG below a total gain of about 8.0 to 9.0x, "the sensor does not fully saturate in those conditions, even
  with massive overexposure"; "the sensor can be 'sort of saturated' in the sense that it is at the maximum value it is
  going to get to at those gain settings, but it will not be responding correctly to the differences in brightness". ZWO
  switches to HCG at about 120 to 130 on its scale with a workaround he suspects is incomplete; he recommends about 200.
  He showed it on an Altair 115M (IMX492) forced into HCG at 4.5x (130 on ZWO's scale): massive overexposure gave a
  histogram that looked nearly normally exposed. His test: overexpose massively and expect a single spike at the far right.
- **Relevance to us:** the one documented case, for cameras we use (ASI294MM, QHY294C), of an analog limit below the ADC
  maximum with a compressed response near it. It would give a top below full scale, a level that may vary between pixels,
  and compression of near-core pixels in every sub, i.e. H2. One author, one camera shown, not peer reviewed.
- **Prediction:** IMX294/IMX492 masters with GAIN 120 to about 190 (the Leo Triplet ASI294MM master, core maximum 0.84 to
  0.93 of full scale, is the first to check) have raw subs whose saturated cores sit below 65532 with a spread between
  pixels; at GAIN below 120 or at about 200 and above they sit at 65532.

### 2.9 Alarcon et al. 2023, scientific CMOS sensors in astronomy: IMX455 and IMX411

- **Citation:** M. R. Alarcon, J. Licandro, M. Serra-Ricart, E. Joven, V. Gaitan, R. de Sousa, "Scientific CMOS Sensors
  in Astronomy: IMX455 and IMX411", PASP 135, 055001 (2023). arXiv:2302.03700.
- **Verified:** method section read (3.3, photon transfer curve and linearity; table 3).
  https://doi.org/10.1088/1538-3873/acd04a
- **What:** QHY600M Pro (IMX455) and QHY411M (IMX411), native 16-bit. A line fitted between 100 and 60,000 ADU: "the
  deviation from linearity is less than 2% up to the saturation point". Figure 9 gives saturation levels of 65532 and
  65523 ADU at the standard modes, with full wells of 50.0 and 67.1 ke. The fixed-pattern (PRNU) factor is 0.55 and
  0.31 percent, the dominant noise above 50,000 ADU on the QHY600.
- **Relevance to us:** a Sony back-illuminated sensor of our generation clips at the digital maximum and is linear to
  2 percent right up to it: the premise of the hard-clipped render. A PRNU of 0.3 to 0.6 percent bounds the pixel-to-pixel
  ripple a flat leaves on a calibrated plateau.
- **Prediction:** single subs from our IMX533 and IMX585 cameras at unity gain show peak against aperture flux linear to
  2 percent up to a sharp break.

### 2.10 Wang and Theuwissen 2017, linearity analysis of a CMOS image sensor

- **Citation:** F. Wang, A. Theuwissen, "Linearity analysis of a CMOS image sensor", Electronic Imaging 2017 (Image
  Sensors and Imaging Systems), pp. 84 to 90. DOI 10.2352/ISSN.2470-1173.2017.11.IMSE-191.
- **Verified:** full text read (author copy). https://harvestimaging.com/pubdocs/213_EI2017.pdf
- **What:** in a voltage-mode 4T pixel the nonlinearity comes from the source follower's signal-dependent gain and the
  floating diffusion capacitance, and "FD capacitances decrease with the output voltage of the pixel"; a CCD's nonlinearity
  "can be as low as a few tenths of a percent", while CMOS "can achieve a nonlinearity of several percent". Measured on a
  0.18 um test chip with 15 um pixels, not an astronomical camera.
- **Relevance to us:** the generic mechanism for a per-sub soft response near the top, in the analog chain, independent of
  the ADC. It matters only where the analog range is used to its end before the ADC clips, which on the ZWO-type cameras is
  gain 0 (2.3 to 2.5).
- **Prediction:** if H2 contributes, single subs at gain 0 show the peak-to-aperture ratio falling by a few percent before
  the clip, and subs at unity gain do not.

### 2.11 Chao et al. 2013, blooming and antiblooming in 1.1 um-pixel CMOS sensors

- **Citation:** C. Chao, K.-Y. Chou, C. Liu, Y.-C. Chen, H.-Y. Tu, H.-Y. Cheng, F.-L. Hsueh, S.-G. Wuu (TSMC), "Blooming
  and Antiblooming in 1.1um-Pixel CIS", International Image Sensor Workshop 2013, paper 01-2.
- **Verified:** full text read (4 pages).
  https://imagesensors.org/Past%20Workshops/2013%20Workshop/2013%20Papers/01-2_004-Chao_paper.pdf
- **What:** CMOS pixels bloom: "excess photo carriers in over-saturated pixels spill into adjacent pixels"; in their colour
  test chips the summed R, Gr, Gb and B response stayed linear after one colour saturated (charge moved, not lost). A nearly
  saturated pixel shows three discrete saturation levels depending on whether its neighbours are saturated (an
  "electrostatic barrier lowering effect of unsaturated photodiodes on nearly saturated neighbors"). Antiblooming through
  the transfer gate costs full well; "anisotropic blooming" between rows and columns appears in shared-pixel designs. Their
  analog chain was designed so that the saturation levels are not clipped by the ADC.
- **Relevance to us:** where the well, not the ADC, limits, a pixel's saturation level depends on its neighbours, so the
  edge of a saturated core does not saturate at the level of its middle. My reading is that unsaturated neighbours lower
  the level, which would dome a top; blooming moves charge outwards, which would widen a plateau. 1.1 um pixels against our
  2.3 to 4.6 um, so the size does not transfer.
- **Prediction:** only on subs taken at gain about 0: a top domed by a few percent, with a row/column asymmetry if the
  blooming is anisotropic. At unity gain none of this can show, since the ADC clips well before the well fills.

### 2.12 Clark, digital camera sensor performance summary

- **Citation:** R. N. Clark, "Digital Camera Sensor Performance Summary", clarkvision.com.
- **Verified:** documentation read in part (the passages on full well and 12-bit converters; I did not find a date).
  https://clarkvision.com/articles/digital.sensor.performance.summary/
- **What:** "At ISO 100, the Canon 1D MII records a maximum of 52,300 electrons; at ISO 50, 79,900 electrons are recorded,
  but that occurs about 3/4 of the 12-bit linear scale."
- **Relevance to us:** the opposite case to ZWO's gain 0: at the lowest gain the well or the analog chain runs out at three
  quarters of the digital range, so a saturated core sits below full scale for a sensor reason, not a calibration one.
- **Prediction:** a camera whose saturated cores sit at the same fraction below full scale in every raw sub, not only in
  the master, is well-limited at that gain.

### 2.13 Revalski et al. 2025, the WFC3/UVIS saturation map from a million stars

- **Citation:** M. Revalski, I. Rivera, V. Bajaj, F. Dauphin, "Updates to the WFC3/UVIS Saturation Map", WFC3 Instrument
  Science Report 2025-06 (STScI). arXiv:2510.00097.
- **Verified:** method section read (abstract, introduction, methods). https://arxiv.org/abs/2510.00097
- **What:** the saturation level of each detector region is the breakpoint of peak flux against 3x3 aperture flux: "This
  ratio is constant as flux accumulates until the central pixel reaches saturation, at which point the ratio changes
  sharply." The full well varies by 13 percent across the detector (63,465 to 72,356 e), from the thickness of the
  silicon; the old constant threshold of 65,500 e was wrong over 87 percent of the detector. Saturation is flagged on the
  raw files, before flat-fielding.
- **Relevance to us:** a CCD in space, but the method is the measurement we lack and needs only stars we already have:
  peak against aperture flux for unsaturated to barely saturated stars, binned by position, gives the knee, its sharpness
  and its spatial variation, in raw subs and in masters.
- **Prediction:** on a master, the breakpoint follows 1/flat across the field if calibration sets it (3.1) and is flat if
  the sensor does. On single raw subs, a sharp break means hard clipping, so any compression in the stack is stack-made
  (H1); a gradual bend means a sensor knee (H2).

## 3. Calibration and normalisation: what one clip level becomes

### 3.1 Dohm-Palmer et al. 2000, the effective saturation level after flat-fielding

- **Citation:** R. C. Dohm-Palmer, M. Mateo, et al., "Mapping the Galactic Halo. II. Photometric Survey", AJ 120, 2496
  (2000). arXiv:astro-ph/0008003.
- **Verified:** method section read (3.3, the reductions per camera). https://doi.org/10.1086/316832
- **What:** "while the saturation level is constant across the chip, the illumination is not. There is a large flat-field
  correction at the edges compared to the center because of vignetting. Thus, after flat-fielding the images, there is a
  large variation in the effective saturation level." Bright unsaturated stars near the edges "are pushed above the
  saturation level by the flat-field correction". Their fix: set saturated pixels to a very high value before
  flat-fielding. For the BTC and Mosaic cameras "the flat-field correction varies less across a single chip (10%)".
- **Relevance to us:** a calibrated pixel clips at (ADC maximum minus dark) divided by the flat. With the flat normalised
  to its mean, the clip falls below 1 where the flat is above 1 (the centre) and rises where it is below 1 (the corners).
  Leo's core maxima, 0.84 to 0.93, spread by 11 percent **(my arithmetic)**, an ordinary vignetting range.
- **Prediction:** across one master's strongly overexposed stars, log(core max) against log(master flat at the star)
  has a slope near -1; what is left after that is the sensor's and the stack's.

### 3.2 Bertin, SWarp user's guide: a co-add's saturation level after rescaling

- **Citation:** E. Bertin, "SWarp v2.16.4 User's guide" (TERAPIX, IAP).
- **Verified:** documentation read (configuration table, resampling section, the combining section). Read from a copy
  hosted at the University of Hertfordshire, not from astromatic.net. https://star.herts.ac.uk/~pwl/Lucas/rho_oph/swarp.pdf
- **Code / licence:** SWarp is open source; I did not read its licence.
- **What:** "The saturation level of the combined data (in ADU) is also computed if saturation values are provided in the
  input image headers ... For any COMBINE TYPE the output saturation level is defined as the minimum of all input
  saturation values after astrometric and photometric rescaling have been applied." On resampling, large Lanczos kernels
  give "extended ripples (Gibbs' phenomenon). These ripples are obvious on the saturation trail".
- **Relevance to us:** the professional co-add tool states the mechanism outright: each input's clip is rescaled by its own
  photometric scale, so a co-add has as many clip levels as inputs, and the only safe single number is the lowest. Our
  per-sub normalisation does the same: sub i clips at (C - o_i) k_i in the master's units, for ADC clip C, offset o_i and
  scale k_i.
- **Prediction:** the spread of (C - o_i) k_i over a session, computed from the stacker's own normalisation record, sets
  the width of the stacked knee (A.3); a steady night gives a narrow knee.

### 3.3 Waters et al. 2020, Pan-STARRS pixel processing: detrending, warping, stacking

- **Citation:** C. Z. Waters, E. A. Magnier, P. A. Price, et al., "Pan-STARRS Pixel Processing: Detrending, Warping,
  Stacking", ApJS 251, 4 (2020). arXiv:1612.05245.
- **Verified:** method section read (saturation, persistence, and the stacking normalisation and rejection).
  https://doi.org/10.3847/1538-4365/abb82b
- **What:** saturation "varies from chip to chip and cell to cell"; of 3840 cells "the median saturation level is 60,400;
  95% have saturation levels > 54,500 DN; 99% have saturation levels > 41,000 DN". Saturated cores are masked with a
  radius that grows with magnitude. Each input is normalised by zero point, transparency, exposure time and airmass. "Pixels
  in the wings of bright stars are liable to be over-rejected as the image quality changes because the flux observed at a
  given position varies as its location on the stellar profile changes"; all inputs are convolved to a common PSF before
  the outlier test.
- **Relevance to us:** (a) on a CCD mosaic the saturation level differs by readout channel by up to tens of percent; a
  digital clip does not, and I found nothing published on column-to-column saturation in our CMOS cameras. (b) Seeing
  variation interacts with pixel rejection exactly at bright stars, so a stacked bright star is not the field PSF scaled,
  even below the clip.
- **Prediction:** stacking one session with rejection on and off changes the near-core profile of bright stars, and the
  halo excess, by more than the noise; if it does not, rejection is not part of the story.

### 3.4 Siril documentation, stacking: normalisation and rejection

- **Citation:** Siril documentation, "Stacking" (readthedocs, latest).
- **Verified:** documentation read. https://siril.readthedocs.io/en/latest/preprocessing/stacking.html
- **Code / licence:** Siril is open source (GitLab free-astro/siril); I did not read its licence file.
- **What:** normalisation is additive, multiplicative, or either "+ scaling" for "dispersion matching"; "Siril uses IKSS
  estimators of location and scale to compute normalisation" (median and MAD for "Faster normalisation"). Rejection:
  percentile, sigma, MAD, median sigma, winsorized sigma, generalized ESD, linear fit clipping. "If Output Normalisation is
  checked, the final image will be normalized in the [0, 1] range".
- **Relevance to us:** the common amateur practice: every sub's clip moves by an offset and a scale fitted to the whole
  frame's statistics, not to a star's flux. With a scale fitted to dispersion, a sub with a brighter, noisier sky is scaled
  down and its clip with it, so the clip spread follows the sky as well as the transparency. Output normalisation also
  means a master's "full scale" is no longer the ADC's.
- **Prediction:** our sessions' per-sub scales and offsets span tens of percent on nights with a moving Moon or changing
  light pollution and a few percent on steady nights (to be measured; Appendix B).

### 3.5 Gawiser et al. 2006, MUSYC: a stack's empirical saturation level

- **Citation:** E. Gawiser, P. G. van Dokkum, D. Herrera, et al., "The Multiwavelength Survey by Yale-Chile (MUSYC): Survey
  Design and Deep Public UBVRIz' Images and Catalogs of the Extended Hubble Deep Field-South", ApJS 162, 1 (2006).
  arXiv:astro-ph/0509202.
- **Verified:** method section read (section 4, data reduction). https://doi.org/10.1086/497644
- **What:** the final stacked images carry "SATUR_LEVEL, the empirically determined saturation level in each image, which
  is usually a factor of a few less than the apparent saturation level of the brightest stars".
- **Relevance to us:** in a weighted stack of many exposures, bright stars stop being linear far below the top the
  brightest of them reach. Their inputs varied more than ours (several nights, weights), but the direction is the one we
  see: compression starts well below the master's maximum, which is what H1 needs.
- **Prediction:** in our masters the breakpoint of peak against aperture flux (2.13) lies below the core maximum of the
  most overexposed stars, by about the per-sub clip spread in the master's units.

### 3.6 Morganson et al. 2018, the DES image processing pipeline

- **Citation:** E. Morganson, R. A. Gruendl, F. Menanteau, et al. (DES Collaboration), "The Dark Energy Survey Image
  Processing Pipeline", PASP 130, 074501 (2018). arXiv:1801.03177.
- **Verified:** method section read (detrending, 4.2 saturation and bleed trail masking, the coadd).
  https://doi.org/10.1088/1538-3873/aab4ef
- **What:** DECam CCD nonlinearity "at both low fluxes and near saturation" is corrected with a per-CCD lookup table;
  saturation values "are different for each amplifier", typically "175,000 photo-electrons (44,000 ADU)"; a saturated star
  and its bleed trail are masked out to where the flux drops below 1 sigma over the background; masked pixels around
  saturated regions are "not included in the final coadd", and coadd objects masked in all inputs are flagged,
  "predominantly set for saturated objects".
- **Relevance to us:** surveys correct the near-saturation range as nonlinear, per detector, before anything else, and they
  keep saturated pixels out of the co-add, so a survey co-add has no clipped plateau, only a hole or an interpolation. Our
  masters average the clipped values, so survey co-add shapes are not a reference for our tops.
- **Prediction:** none on our data.

## 4. Rejection, resampling and seeing

### 4.1 Conejero 2015, PixInsight forum: Clip high range on saturated cores

- **Citation:** J. Conejero (PixInsight), post of 20 February 2015 in "Going from Maxim 5.23 to PI", PixInsight Forum.
- **Verified:** documentation read (the forum thread).
  https://pixinsight.com/forum/index.php?threads/going-from-maxim-5-23-to-pi.7971/
- **What:** "If star cores are saturated in all of the images, this option will reject them and yield zero. Anyway, Clip
  high range is disabled by default in ImageIntegration." (The black cores in that thread came from a BZERO problem in
  files calibrated elsewhere.)
- **Relevance to us:** PixInsight's range rejection drops values above a threshold as saturated. Where some subs saturate,
  the stack becomes the mean of the others, lower than the clip; where all do, nothing is left. It is off by default, and
  our masters come from TianWen's stacker, so it describes a design option, not our data: excluding per-sub clipped values
  is the amateur form of what the surveys do (3.6, 4.2). I could not reach PixInsight's ImageIntegration documentation for
  the threshold's default (section 7).
- **Prediction:** a TianWen stack that left out per-sub values at the clip would raise barely saturated cores toward their
  linear value and, if H1 holds, shrink the halo excess.

### 4.2 Rubin Observatory, LSST Science Pipelines: saturation masked before calibration, kept out of the co-add

- **Citation:** Rubin Observatory LSST Data Management, `ip_isr` (`python/lsst/ip/isr/isrTask.py`) and `drp_tasks`
  (`python/lsst/drp/tasks/assemble_coadd.py`), GitHub, main branch.
- **Verified:** source read (configuration fields and docstrings).
  https://github.com/lsst/ip_isr and https://github.com/lsst/drp_tasks
- **Code / licence:** GPL-3.0 or later (stated in the `drp_tasks` file header).
- **What:** ISR: `doSaturation` masks SAT ("NB: this is totally independent of the interpolation option"),
  `growSaturationFootprintSize` default 1, `doSaturationInterpolation` default True ("Perform interpolation over pixels
  masked as saturated?"), `doWidenSaturationTrails` default True. Co-add: `badMaskPlanes` includes SAT;
  `maskPropagationThresholds` default {"SAT": 0.1}, "we set the mask bit on the coadd if the fraction the rejected frames
  would have contributed exceeds this value".
- **Relevance to us:** Rubin decides saturation per raw pixel, grows it by a pixel, interpolates it within the visit and
  leaves it out of the co-add, so a co-add pixel in the partial-saturation zone is a mean over unsaturated inputs: the
  stack-made knee of H1 cannot form there.
- **Prediction:** a per-sub SAT mask in TianWen's stacker is the switch for the H1 test in section 6: with it, a stack-made
  compression disappears; a sensor knee (H2) would not, since the compressed values are below the clip and unmasked.

### 4.3 Bosch et al. 2018, the Hyper Suprime-Cam software pipeline

- **Citation:** J. Bosch, R. Armstrong, S. Bickerton, et al., "The Hyper Suprime-Cam software pipeline", PASJ 70 (SP1), S5
  (2018). arXiv:1705.06766.
- **Verified:** method section read (CCD processing, ISR, defect repair, brighter-fatter correction, safe clipping).
  https://doi.org/10.1093/pasj/psx080
- **What:** ISR flags "pixels exceeding the saturation level (either the full well depth or the range of the
  analog-to-digital converter ...) ... as SAT"; saturated pixels are repaired "by using a linear predictive code to
  interpolate the values of nearby good pixels". For PSF co-addition "the operation used to combine all input pixels at
  each point on the coadd image must be strictly linear: robust estimators such as the median or sigma-clipped means cannot
  be used". The brighter-fatter effect in thick CCDs causes "brighter stars to be larger and more elliptical than faint
  stars".
- **Relevance to us:** (a) the same two limits as ZWO's charts, well or ADC. (b) A sigma-clipped co-add has no
  well-defined PSF, so a stacked bright star need not be the field PSF scaled. (c) Brighter-fatter makes a bright star's
  halo grow with brightness; our excess shrinks with overexposure, the opposite sign, and brighter-fatter is a CCD effect.
- **Prediction:** the sign is the test, and it is already in: Spearman -0.4 to -0.6 on five masters rules out
  brighter-fatter as the cause of the halo excess.

### 4.4 PixInsight, interpolation algorithms: Lanczos clamping

- **Citation:** PixInsight Development Team, "Interpolation Algorithms in PixInsight" (reference documentation; first
  written 2011, since rewritten).
- **Verified:** documentation read.
  https://pixinsight.com/doc/docs/InterpolationAlgorithms/InterpolationAlgorithms.html
- **What:** Lanczos "accumulates the positive and negative parts of the convolution separately"; the clamping threshold
  default "in every geometric and image registration process is 0.3"; "Undershoot is a problem of undersampled images";
  clamping "reduces undershoot by a factor of two to three, at a cost in photometric accuracy of one to four percent of the
  flux of a star" (a synthetic field rotated 1.5 degrees, FWHM 1.8 and 3.5 px).
- **Relevance to us:** interpolating a clipped plateau with clamped Lanczos leaves it flat, as our test found (first ring
  0.995 of the max). The clamp's 1 to 4 percent flux cost goes into the ring around the top, not the top.
- **Prediction:** none beyond our test; real stars pass the warp and injected ones do not, so a 1 to 4 percent difference
  in the first ring of unsaturated stars between real and injected would be the clamp (my reasoning).

### 4.5 PixInsight Class Library, LanczosInterpolation.h

- **Citation:** PixInsight Class Library, `include/pcl/LanczosInterpolation.h` (GitLab, master).
- **Verified:** source read. https://gitlab.com/pixinsight/PCL/-/raw/master/include/pcl/LanczosInterpolation.h
- **Code / licence:** PixInsight Class Library License Version 2.0.
- **What:** samples accumulated by sign (`sp`, `wp` and `sn`, `wn`); r = sn/sp; if r >= 1 return sp/wp; if r passes the
  threshold, sn and wn are scaled by 1 - ((r - t)/(1 - t))^2; default threshold 0.3F. "Lanczos interpolation generates
  strong undershoot (aka ringing) artifacts when the negative lobes of the interpolation function fall over bright isolated
  pixels or edges."
- **Relevance to us:** inside a plateau every sample is the same positive value and the result is exactly the plateau; at
  its edge the negative lobes are damped. Nothing in the kernel rounds a top. TianWen mirrors this rule (at a threshold of
  0.7).
- **Prediction:** as 4.4.

### 4.6 Fruchter and Hook 2002, Drizzle

- **Citation:** A. S. Fruchter, R. N. Hook, "Drizzle: A Method for the Linear Reconstruction of Undersampled Images", PASP
  114, 144 (2002). arXiv:astro-ph/9808087.
- **Verified:** method section read (sections 1 and 2). https://doi.org/10.1086/338393
- **What:** shift-and-add "convolves the image yet again with the original pixel"; the final image is T * O * E * P * G
  (true image, optics, detector pixel response, pixel shift, output grid); drizzle "replaces the convolution by P ... with a
  convolution with p, the pixfrac", and "convolutions add roughly as a sum of squares".
- **Relevance to us:** a drizzled master's star is each sub's star convolved with the drop. Convolving a clipped plateau
  with a box of width d shrinks its flat part by d and ramps its edge over d **(my arithmetic)**. In a Bayer drizzle each
  colour is sampled every other photosite, so the drop is large against the colour's own sampling. Lanczos, by contrast,
  is close to an identity on a plateau (4.4).
- **Prediction:** OSC masters made by Bayer drizzle have plateaus about one drop width narrower than the same session
  demosaiced and warped; the 24 mm lens master (plateau 3 px rendered, 2 px real), if OSC, should be checked for this
  first, since the render draws on the master grid and convolves with nothing.

### 4.7 Racine 1996, temporal fluctuations of atmospheric seeing

- **Citation:** R. Racine, "Temporal Fluctuations of Atmospheric Seeing", PASP 108, 372 (1996).
- **Verified:** full text read (ADS scan). https://doi.org/10.1086/133732
- **What:** 414 SCIDAR profiles over 20 nights on Mauna Kea, free atmosphere only. Seeing is log-normal with a dispersion
  of 0.175 dex ("a seeing ratio of 1.50"); "significant variations are observed on time scales of minutes"; the mean
  normalised difference between two seeing values grows with an e-folding time of 17 minutes; the average future seeing
  differs from the present by a factor 1.56; the 10 to 90 percentile range spans a factor 3.8. "Continental sites may
  exhibit significantly different behaviors."
- **Relevance to us:** over a 2 to 6 hour session the seeing part of the FWHM changes by tens of percent, not 5; in our
  FWHM it is diluted by optics, guiding and sampling (Appendix B). At fixed flux a star's peak goes as FWHM^-2, so its
  log spread is twice the FWHM's. A barely saturated star is clipped in its sharp subs and not its soft ones.
- **Prediction:** the per-sub FWHM of our sessions (the stacker's frame metrics) spreads by 10 to 30 percent (log sigma);
  the render's 5 percent is low for barely saturated stars and harmless for strongly overexposed ones (A.4, A.5).

## 5. Repairing and modelling saturated stars

### 5.1 Siril: finding the saturated part, fitting without it, and unclipstars

- **Citation:** Siril documentation, "Dynamic PSF" and "Desaturate Stars"; Siril tutorial "Synthetic stars"; Siril source
  `src/algos/star_finder.c` (GitLab, master).
- **Verified:** documentation read; source read (the saturation test).
  https://siril.readthedocs.io/en/stable/Dynamic-PSF.html,
  https://siril.readthedocs.io/en/latest/processing/stars/unclipped.html, https://siril.org/tutorials/synthetic-stars/,
  https://gitlab.com/free-astro/siril/-/raw/master/src/algos/star_finder.c
- **Code / licence:** open source; licence file not read.
- **What:** findstar checks whether "the core around the maxima is saturated, i.e. consistently close to the upper bound of
  the dynamic range", and if so runs "an edge-walking algorithm to detect the limit of the saturated part". In the source,
  a candidate is saturated when its high pixels pass `SAT_THRESHOLD` 0.7 of the dynamic range (frame max minus background),
  and the plateau is every pixel within `SAT_DETECTION_RANGE` 0.1 of the dynamic range below the local maximum. "Since
  version 1.2.0, the saturated part of the star is removed from the fitting process". `unclipstars` "Re-profiles clipped
  stars of the loaded image to desaturate them, scaling the output so that all pixel values are <= 1.0"; the tutorial:
  "The clipped pixels in the saturated star are replaced with the intensity from the synthesized PSF". Linear data only.
- **Relevance to us:** Siril's saturated part reaches 10 percent of the dynamic range below the maximum, five times our
  2 percent plateau, and the whole of it is left out of the fit. If the stack (H1) or the sensor (H2) compresses pixels
  between 0.9 and 0.98 of the max, a fit that admits them reads a low amplitude (A.5); Siril's margin guards against that.
- **Prediction:** refitting R0's saturated stars with Siril's 10 percent exclusion instead of 2 percent lowers the halo
  excess of barely saturated stars, under either hypothesis, and changes little for strongly overexposed ones.

### 5.2 Stetson 1987, DAOPHOT

- **Citation:** P. B. Stetson, "DAOPHOT: A computer program for crowded-field stellar photometry", PASP 99, 191 (1987).
- **Verified:** method section read (FIND's treatment of bad pixels; the robust weighting test). ADS scan;
  https://doi.org/10.1086/131977
- **What:** "brightness values which are found during reduction to be above the known saturation level of the chip ...
  are omitted from the fits"; a robust weighting then down-weights pixels with large residuals.
- **Relevance to us:** the classical rule: one known level per chip, everything above it omitted. It assumes one level and a
  linear response up to it; in a stack neither holds (3.1, 3.2, 3.5).
- **Prediction:** none new.

### 5.3 Bertin, SExtractor and PSFEx documentation: saturation flags and a 10 percent margin

- **Citation:** SExtractor documentation, "Flagging"; PSFEx documentation, "Getting started".
- **Verified:** documentation read. https://sextractor.readthedocs.io/en/latest/Flagging.html,
  https://psfex.readthedocs.io/en/latest/GettingStarted.html
- **What:** SExtractor flag 4: "at least one object pixel is saturated". "PSFEx requires SExtractor to flag all saturated
  sources, which may otherwise contaminate the 'clean' star sample used to compute the PSF model"; where the header level is
  unreliable, set `SATUR_LEVEL` to "about 10% lower than the lowest value derived from the visual examination of all
  images".
- **Relevance to us:** the PSF modelling tool's own rule puts the effective saturation 10 percent below where saturation is
  seen: the same direction as Siril's 10 percent and MUSYC's "factor of a few".
- **Prediction:** as 5.1.

### 5.4 RC-Astro, StarXTerminator usage notes

- **Citation:** RC-Astro, "StarXTerminator Usage Notes" and the StarXTerminator product page.
- **Verified:** documentation read; neither page says anything about clipped cores.
  https://www.rc-astro.com/starxterminator-usage-notes/, https://www.rc-astro.com/software/sxt/
- **What:** "StarXTerminator is trained on images stretched using a simple midtones transfer function (MTF)"; "Any
  processing that significantly alters star profiles relative to this method may reduce the effectiveness and/or quality
  of star removal." AI version 11 (15 September 2022) matches noise statistics "when removing large stars".
- **Relevance to us:** the leading commercial remover ties its quality to the star profiles of its training data and
  publishes nothing on saturated cores. For us the same holds: injected saturated stars must look as the stack makes them.
- **Prediction:** none.

## 6. What it predicts for the injector's virtual-sub render

**Why eight hard-clipped subs now reproduce the tops.** Our subs clip at the ADC maximum at nearly every gain we use (2.3
to 2.5, 2.9), so a hard clip per sub is the right primitive, and for strongly overexposed stars the top is insensitive to
every scatter the render draws (A.4). Once the amplitude and the shape come from the same profile, the tops agree. The
remaining 0.09 to 0.11 first-ring gap on two masters is where a larger per-sub spread (A.3) would show first.

**H1 against H2, for the halo excess.**

| | H1: stack-made compression | H2: sensor knee |
|:-|:-|:-|
| Literature for it | seeing log sigma 0.175 dex within nights (4.7); per-sub rescaled clips (3.2, 3.3, 3.4); a stack's real saturation "a factor of a few" below its top (3.5); over-rejection at bright stars as image quality changes (3.3) | CMOS nonlinearity "several percent" (2.10); neighbour-dependent saturation and blooming (2.11); IMX294/IMX492 in HCG below 8x (2.8); a well that runs out below full scale (2.12); four photosites per IMX294 pixel (2.7) |
| Literature against it | none found | the ADC clips first above gain 0 on every chart (2.3 to 2.5); linear to 2 percent up to 65532 (2.9) |
| Size | core depletion 9, 19, 31 percent for FWHM scatter 0.1, 0.2, 0.3; real over field 1.10, 1.24, 1.45 if the fit reads the core (A.5) | a few percent, unless the IMX294 HCG case applies |
| Trend with overexposure | negative: the knee moves out toward the seeing-insensitive radius (A.6) | negative only through the fit window's geometry; no reason for the same slope on every camera |
| Single raw subs | linear to a sharp break at the ADC maximum | a bend below the maximum, at a fixed raw level |
| Gain (FITS GAIN card) | no dependence | only at gain about 0, or IMX294/IMX492 at 120 to about 190 |
| Restack with FWHM-matched subs | excess shrinks | unchanged |
| Restack with a per-sub SAT mask (4.2) | excess shrinks | unchanged |

**The tests, cheapest first.**

- **T1, the gain split.** Read GAIN, XBINNING and the camera from every master's subs. If the five masters with the
  negative trend span gains well above 0 on ZWO-type cameras, H2 cannot be the main cause there (2.3 to 2.5).
- **T2, the single-sub breakpoint (decisive).** In the raw subs of those five sessions, for every star from unsaturated
  to barely saturated, plot the peak (or the central 3x3) against the flux in the 2.5 to 4 px annulus, which never clips.
  A straight line to a sharp corner at 65520 or 65532 is H1; a curve bending below the line before the maximum is H2, and
  the raw level of the bend is the sensor's knee (2.13).
- **T3, the FWHM-matched restack.** Restack one session twice: all subs, and only the subs within 5 percent of the median
  FWHM. H1 predicts the halo excess of barely saturated stars falls toward 1 in the second; H2 predicts no change.
- **T4, the exclusion margin.** Refit R0's saturated stars with the plateau defined as within 10 percent of the maximum
  (Siril, 5.1) instead of 2 percent. Under either hypothesis the excess of barely saturated stars should fall; if it does
  not, the excess is in the profile (the other review's question), not in the saturation.
- **T5, the flat.** Regress each master's core maxima on the master flat at the star (3.1). A slope near -1 explains the
  star-to-star spread of the maximum, and tells the injector to take its clip from the flat at the injection site.

**If T2 and T3 point to H1, change the render, in this order.**

1. **Per-sub clip levels from the session.** Sub i clips at (C - o_i) k_i / F(x, y), from the stacker's normalisation
   record and the master flat at the site (3.1, 3.2), rescaled to the master's units, instead of one level for all subs.
2. **Per-sub widths from the session.** Draw widths from the session's own FWHM distribution (log sigma from the frame
   metrics, likely 0.1 to 0.3 rather than 0.05), and amplitudes at fixed flux, so a sub's peak goes as width^-2.
3. **More subs where the knee matters.** With eight subs a pixel drops below the 2 percent plateau as soon as one sub
   falls 16 percent short (A.7); with the session's 20 to 300 subs it takes many. Use the session's N, or at least 32, for
   barely saturated stars.
4. **Noise before the clip.** Add each sub's shot and read noise before clipping: E[min(X, c)] is 0.4 sigma below c where
   the mean signal is at c, 0.15 to 0.6 percent of the clip on our cameras (A.8). Small, but inside the 2 percent plateau
   test.
5. **The donor's clip from strongly overexposed stars only.** A barely saturated donor's maximum lies on the knee, below
   the stack's real top, so clipping every virtual sub there makes the plateau too wide and too low. Take the clip level
   from the master's strongly overexposed stars (corrected by the flat), as MUSYC's distinction between the apparent and
   the empirical saturation level suggests (3.5).

**If T2 points to H2 (a sensor knee) on some camera and gain,** model the knee per sub for that camera and gain only, from
the measured raw breakpoint, and leave the rest hard-clipped. **The two exceptions** are checked by T1 and T5 (the
ASI1600MM master: if its GAIN is about 0, a sensor dome, 2.10 and 2.11; if at unity, a large per-sub clip spread, A.3) and
by the OSC smoothing of 4.6 together with the airmass range across a 24 mm field (Appendix B) for the lens master.

## 7. Leads I could not verify, or verified only in part

- **Janesick 2007, Photon Transfer** (DOI 10.1117/3.725073) and **Janesick et al. 2007, "Fundamental performance
  differences between CMOS and CCD imagers: Part II"**, Proc. SPIE 6690, 669003 (DOI 10.1117/12.740218): metadata only.
  Search snippets claim the second treats CMOS nonlinearity near saturation through the sense node's capacitance; I could
  not read either.
- **Fossum and Hondongwa 2014, "A Review of the Pinned Photodiode for CCD and CMOS Image Sensors"**, IEEE JEDS 2(3), 33
  (DOI 10.1109/JEDS.2014.2306412): metadata and the one-line abstract only; the open-access PDF did not download. It would
  say how a pinned photodiode's full well and overflow behave.
- **Kodak application note MTD/PS-0898, "Photodiode charge capacity and antiblooming"** (2009), cited by Chao et al.: not
  reached.
- **Hirakawa and Parks 2005, adaptive homogeneity-directed demosaicing** (IEEE TIP 14(3), 360; DOI 10.1109/TIP.2004.838691):
  metadata only. What AHD does to a clipped 2 to 3 px plateau is my reasoning in 4.6, not the paper's.
- **PixInsight ImageIntegration and HDRComposition documentation**: the reference pages returned 404 on every URL I tried;
  the defaults of range rejection ("Range high") and HDRComposition's binarizing threshold (0.8 according to a third-party
  tutorial) are unverified. The PixInsight forum thread on HDRComposition did not describe the algorithm.
- **PixInsight's RepairedHSVSeparation script**: only third-party tutorials found; no primary documentation.
- **Cloudy Nights threads on the maximum ADU an ASI294MC reaches when saturated** (one search snippet mentions about
  55,500): the site refused automated fetches (HTTP 403).
- **ZWO's 2025 ASI585MC/MM Pro manual**, which search results say gives 40 ke for the full well against 47 ke in the 2022
  manual I read: not read.
- **Player One Uranus-C and SVBONY SV605CC data, Sony IMX585, IMX533 and IMX492 datasheets**: not reached (Sony's full
  datasheets are not public).
- **Gilliland et al. 2010, the earlier WFC3 saturation map**, cited by Revalski et al.: not read.
- **Atmospheric extinction coefficients** (King 1985, ING Technical Note 31): the PDF did not download, and the ING
  observing guide page I reached gave no per-band table; the extinction figure in Appendix B is an assumption.
- **Amateur measurements of per-sub FWHM scatter over a night, and of registration residuals**: none found that I could
  verify; both should come from our own frame metrics.

## Appendix A. My arithmetic

**A.1 Gain charts.** e-/ADU at gain 0 read off the ZWO charts, times 2^bits: ASI1600 5.0 x 4096 = 20,480 e (stated 20k);
ASI294MM bin 2 4.05 x 16384 = 66,355 e (66.387k); bin 1 3.52 x 4096 = 14,418 e (14.417k); ASI533 3.1 x 16384 = 50,790 e
(50k); ASI585 11.5 x 4096 = 47,104 e (47k). The chart values are read by eye to about 2 percent. ZWO gain is in units of
0.1 dB of voltage gain, so gain 60 is a factor 2 and the ADC range there is at most half the well. In a 16-bit file a
12-bit clip is 4095 x 16 = 65520 and a 14-bit clip 16383 x 4 = 65532.

**A.2 Leo's core maxima.** 0.93/0.84 = 1.107: an 11 percent spread, the size of a 10 to 20 percent vignetting profile.

**A.3 Averaging hard clips at spread levels.** Let each sub clip at c_i, uniform on [a, b] = [c(1 - delta), c(1 + delta)],
and let the true normalised signal be S. The stack is V(S) = mean of min(S, c_i): V = S below a, V = c above b, and in
between V = c - (b - S)^2 / (2(b - a)). In units of c: at S = 1, V = 1 - delta/4. The 2 percent plateau (V >= 0.98) needs
S >= 1 + delta - 2 sqrt(0.02 delta): 0.987 for delta 0.05, 1.040 for 0.15, 1.145 for 0.30. A wider spread moves the
plateau's edge inward to where the true signal is higher.

**A.4 Why an overexposed top ignores the scatter.** Moffat, FWHM 3 px, beta 3: alpha = FWHM / (2 sqrt(2^(1/beta) - 1)) =
2.94 px, and the profile at 1 px is p(1) = (1 + 1/alpha^2)^-beta = 0.72. A star with peak 3c has 2.16c at 1 px. For that
pixel to fall below the clip in one sub, the sub's profile must drop to 0.333 there, which needs r/alpha = 0.665, i.e.
r = 1.96 px: the sub must be offset by about 1 px, or its width halved. Against 0.25 px offsets and 5 to 30 percent width
scatter the first ring stays at the top. With a clip spread delta up to 0.5, a first ring at 0.85 of the max needs the
true signal at 1 px at most 0.95c (from A.3), so the star's peak at most 0.95/0.72 = 1.32c: only barely saturated stars
can show a first ring of 0.85 through the clip spread.

**A.5 Core depletion of a barely saturated star.** At fixed flux a star's peak goes as FWHM^-2, so a log-normal FWHM
scatter s gives a log-normal peak scatter 2s. Take a star whose median-sub peak equals the clip (in normalised units 1).
The linear mean of the peaks is exp(2 s^2); the part removed by the clip is E[(P - 1)+] = exp(2 s^2) Phi(2s) - 1/2. For
s = 0.1, 0.2, 0.3 the removed part is 0.091, 0.210, 0.369 against means 1.020, 1.083, 1.197: depletions of 8.9, 19.4 and
30.8 percent. If the amplitude is read off the core pixels, the field profile scaled to it underpredicts every unclipped
radius by the inverse: real over field 1.10, 1.24, 1.45. Assumptions: transparency spread ignored (it widens the
depletion), the fit dominated by the core pixels, pixel integration ignored.

**A.6 The seeing-insensitive radius.** For a Moffat of fixed flux F, S(r) = F (beta - 1) / (pi alpha^2) (1 +
r^2/alpha^2)^-beta, and d ln S / d ln alpha = -2 + 2 beta (r/alpha)^2 / (1 + (r/alpha)^2), which is zero at r =
alpha / sqrt(beta - 1). Inside it a sharper sub is brighter, outside it fainter. FWHM 3 px, beta 3: 2.08 px; FWHM 2 px,
beta 2.5: 1.44 px; FWHM 4 px, beta 3: 2.77 px. A barely saturated star's knee is in the core, where the peak's sensitivity
to seeing is largest (-2); driving it further past the clip moves the knee outward toward this radius, where the
sensitivity is zero, so the stack-made compression and the halo excess fall with overexposure.

**A.7 Eight subs and the 2 percent test.** In a mean of N subs, one sub short of the clip by a fraction f lowers the pixel
by f/N. With N = 8 a single sub 16 percent short drops the pixel below the 2 percent plateau; with N = 100 it takes a
sub 200 percent short, or many short subs.

**A.8 Noise before the clip.** For X normal with mean s and sigma, E[min(X, c)] = s - E[(X - c)+]; at s = c it is
c - 0.399 sigma, at s = c + sigma c - 0.083 sigma, at s = c + 2 sigma c - 0.008 sigma. Shot noise at the clip, sigma/c =
1/sqrt(clip in electrons): ASI1600 at unity (4095 e) 1.56 percent, so 0.4 sigma = 0.63 percent; ASI1600 at gain 0
(20,480 e) 0.28 percent; ASI294MM at gain 0 (66.4k e) 0.16 percent; ASI533 at unity (16,384 e) 0.31 percent; ASI585 at
unity (4096 e) 0.62 percent.

**A.9 Racine's dispersion.** 0.175 dex is a factor 10^0.175 = 1.50, a natural-log sigma of 0.403, over 20 nights on
Mauna Kea's free atmosphere.

## Appendix B. Assumptions

- **Seeing in our FWHM.** Total FWHM^2 = seeing^2 + instrument^2 (optics, guiding, pixel). With seeing 2.5 arcsec and an
  instrument term of 2.0 arcsec, the seeing carries 61 percent of FWHM^2, so a seeing log sigma of 0.2 to 0.4 within a
  night becomes a total FWHM log sigma of about 0.12 to 0.24. Low-altitude continental sites are not Mauna Kea's free
  atmosphere (Racine says so); the real number must come from our frame metrics.
- **Per-sub scale and offset spread.** Tens of percent with a moving Moon or changing light pollution, a few percent on a
  steady night; not measured here.
- **Extinction across a wide field.** An assumed 0.2 mag per airmass; from airmass 1.2 to 2.0 that is 0.16 mag, 14
  percent, and across a 24 mm field that spans tens of degrees in altitude the airmass, and so the transparency, differs
  from one side to the other and changes differently through the night. One global scale per sub cannot equalise it
  everywhere.
- **Flat normalisation.** The master flat is normalised to its mean, so the centre of a vignetted field is above 1.
- **The R0 fit.** That R0's wing fit admits pixels just below its 2 percent plateau; how far down it reaches decides how
  much of A.5's depletion reaches the amplitude.
- **IMX492 structure.** That the IMX492 bins four photosites per bin 2 pixel as the IMX294 does (2.7); inferred from ZWO's
  unlocked 47 MP mode, not documented.
