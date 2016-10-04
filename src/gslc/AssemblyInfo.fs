﻿namespace System
open System.Reflection

[<assembly: AssemblyTitleAttribute("gslc")>]
[<assembly: AssemblyProductAttribute("gslc")>]
[<assembly: AssemblyDescriptionAttribute("Genotype Specification Language")>]
[<assembly: AssemblyVersionAttribute("0.3.0")>]
[<assembly: AssemblyFileVersionAttribute("0.3.0")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.3.0"
    let [<Literal>] InformationalVersion = "0.3.0"
