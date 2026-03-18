namespace TianWen.Lib.Sequencing;

public enum OpticalDesign
{
    Unknown = 0,
    Refractor,
    Newtonian,
    NewtonianCassegrain,
    SCT,
    Cassegrain,
    RASA,
    Astrograph
}

public static class OpticalDesignExtensions
{
    extension(OpticalDesign design)
    {
        /// <summary>
        /// Pure mirror designs and astrographs are CA-free, so focusing on luminance
        /// and applying zero offsets is safe. Designs with refractive elements (corrector
        /// plates, lenses) shift focus per wavelength and need per-filter adjustment.
        /// </summary>
        public bool NeedsFocusAdjustmentPerFilter => design switch
        {
            OpticalDesign.Newtonian => false,
            OpticalDesign.Cassegrain => false,
            OpticalDesign.RASA => false,
            OpticalDesign.Astrograph => false,
            OpticalDesign.Refractor => true,
            OpticalDesign.SCT => true,
            OpticalDesign.NewtonianCassegrain => true,
            _ => true
        };
    }
}
