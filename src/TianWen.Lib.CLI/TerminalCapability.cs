namespace TianWen.Lib.CLI;

public enum TerminalCapability
{
    Unknown = 0,
    Columns132 = 1,
    PrinterPort = 2,
    Sixel = 4,
    // Is 5 Reserved
    SelectiveErase = 6,
    SoftCharacterSet = 7,
    UserDefinedKeys = 8,
    NationalReplacementCharacterSets = 9,
    // Is 10 Reserved?
    // Is 11 Reserved?
    YugoslavianSCS = 12,
    // Is 13 Reserved?
    EightBitInterfaceArchitecture = 14,
    TechnicalCharacterSet = 15,
    // Is 16 Reserved?

    LocatorPort = 16, // Added from https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Device-Control-functions
    TerminalStateInterogation = 17, // Added from https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Device-Control-functions

    WindowingCapability = 18,
    Sessions = 19,
    // Is 20 Reserved?
    HorizontalScrolling = 21,
    Color = 22,
    Greek = 23,
    Turkish = 24,
    // Is 25 Reserved?
    // Is 26 Reserved?
    // Is 27 Reserved?

    RectangularAreaOperations = 28,
    AnsiTextLocator = 29, // Added from https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Device-Control-functions
                          // Is 29 Reserved?
                          // Is 30 Reserved?
                          // Is 31 Reserved?

    TextMacros = 32,
    // Is 33 Reserved?
    // Is 34 Reserved?
    // Is 35 Reserved?
    // Is 36 Reserved?
    // Is 37 Reserved?
    // Is 38 Reserved?
    // Is 39 Reserved?
    // Is 40 Reserved?
    // Is 41 Reserved?


    ISOLatin2CharacterSet = 42,
    // Is 43 Reserved?
    PCTerm = 44,
    SoftKeyMap = 45,
    ASCIIEmulation = 46
}
