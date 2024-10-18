using System;
using System.Collections.Generic;

namespace Astap.Lib.Devices;

using ValueDictRO = IReadOnlyDictionary<string, Uri>;

internal record ProfileDto(Guid ProfileId, string Name, ValueDictRO Values);