﻿module commonTypes
open System
open parseTypes
open pragmaTypes
open constants
open Amyris.Bio.primercore
type Platform = MegaStitch | NoPlatform
open thumperProxyTypes
open uri

/// Instructions gleaned from command line
type ParsedOptions =
   {apeOut: (string*string) option;
    quiet: bool;
    refStrain: string; 
    libDir: string;
    cmOut: (string*string) option;
    platform: Platform;
    thumperOut : string option;
    sbolOut: bool;
    flatOut: string option;
    iter: bool;
    doParallel: bool;
    verbose: bool;
    noPrimers: bool;
    name2IdPath: string option;
    primerFile: string option;
    rabitsOnly: bool;
    docStringFile: string option;
    proxyURL: string option;
    lexOnly: bool}

type DNAIntervalType = ANNEAL | RYSELINKER | AMP | SANDWICH 
type DNAInterval = {il:int; ir:int; iType:DNAIntervalType}

type Breed =
    | B_PROMOTER
    | B_TERMINATOR
    | B_MARKER
    | B_FUSABLEORF
    | B_UPSTREAM
    | B_DOWNSTREAM 
    | B_GST 
    | G_M 
    | G_STOP 
    | B_GS 
    | B_INLINE 
    | B_X 
    | B_VIRTUAL 
    | B_LINKER

/// Used in the grammar of GSL to pick a standard part of a gene (p, t, o etc)
type StandardSlice = 
    | GENE 
    | PROMOTER 
    | TERMINATOR 
    | ORF 
    | FUSABLEORF 
    | UPSTREAM 
    | DOWNSTREAM 
    | MRNA (* ORF + term *)

let sliceTypeChars = ['p', 'u', 't', 'd', 'o', 'f', 'g', 'm']
let charToSliceType c =
    match Char.ToLower c with
    | 'p' -> Some(PROMOTER)
    | 'u' -> Some(UPSTREAM)
    | 't' -> Some(TERMINATOR)
    | 'd' -> Some(DOWNSTREAM)
    | 'o' -> Some(ORF)
    | 'f' -> Some(FUSABLEORF)
    | 'g' -> Some(GENE)
    | 'm' -> Some(MRNA)
    | _ -> None

type SliceType = REGULAR | MARKER | LINKER | INLINEST | FUSIONST
let formatST (s:SliceType) = 
    match s with 
    | REGULAR -> "REG"
    | MARKER -> "MARKER"
    | LINKER -> "LINKER"
    | INLINEST -> "INLINE"
    | FUSIONST ->"FUSION"

/// Represents one piece of DNA for assembly, capturing its origins and relevant details
type DNASlice =
   {id: int option; 
    extId: string option; 
    dna: char array;  
    sourceChr: string; 
    sourceFr: int<ZeroOffset>; 
    sourceTo: int<ZeroOffset>; 
    sourceFwd: bool ;
    sourceFrApprox: bool; 
    sourceToApprox: bool;
    destFr: int<ZeroOffset>; 
    destTo: int<ZeroOffset>; 
    destFwd: bool; 
    /// is this slice created by PCR
    amplified: bool; 
    template: char array option;
    sliceName: string;
    uri: Uri option;
    description: string ; 
    sliceType: SliceType ; 
    pragmas: PragmaCollection; 
    dnaSource: string ; 
    breed: Breed; 
    /// possible existing parts that could implement.  Amyris specific.
    rabitCandidates: RabitCandidate[] 
} 

type AssemblyOut =
   {id: int option; 
    dnaParts: DNASlice list; 
    name: string;
    uri: Uri option; 
    linkerHint: string;
    pragmas: PragmaCollection; 
    designParams: DesignParams; 
    docStrings: DocstringLine list list
    }

/// Model a primer which diverges and has body/tail parts.
/// The body part anneals to the intended amplification target and the tail
/// hangs out and anneals for stitching purposes
type Primer =
    {tail: char array;
     body: char array; 
     annotation: DNAInterval list}
    with
    member x.Primer
        with get() = Array.append x.tail x.body

    member x.lenLE(maxOligo) =
        x.tail.Length + x.body.Length<=maxOligo

    /// Try to find an interval of type iType, returns Some/None
    member x.Interval(iType:DNAIntervalType) =
        x.annotation |> List.tryFind (fun i -> i.iType = iType)
end

/// Divergend pair of Primers
type PrimerPair = {fwd:Primer; rev:Primer; name:string}

type DivergedPrimerPair =
    | DPP of PrimerPair
    | GAP 

type RYSELinker = {name:string; dna:char array}


