using Astap.Lib.Devices.Ascom;

namespace Astap.Lib.Plan
{
    public class Telescope
    {
        public Telescope(string name, int focalLength, CameraBase camera, CoverBase cover)
        {
            Name = name;
            FocalLength = focalLength;
            Camera = camera;
            Cover = cover;
        }

        public string Name { get; }

        public int FocalLength { get; }

        public CameraBase Camera { get; }

        public CoverBase Cover { get; }
    }
}
