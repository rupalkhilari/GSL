﻿module DnaCreation
open System
open shared // common routines used in several modules
open Amyris.Bio.biolib
open constants
open thumper // Thumper output formats, dumping etc
open ryse    // RYSE architecture
open cloneManager
open ape
open l2expline
open parseTypes
open Amyris.Bio.utils
open PrettyPrint
open sgdrefformat
open pragmaTypes
open commonTypes
open applySlices
open thumperProxyTypes
open Amyris.Bio

// flag_new_gsl 8/12/15 ---  top  (paired with bottom)

open Newtonsoft.Json
// flag_new_gsl 8/12/15 --- bottom (paired with top)

// ================================================================================================
// Slice manipulation routines for getting from gene notation down to specific genomics coordinates
// ================================================================================================
let validateMods errorRef (where:ParseRange) (mods: Mod list) =
    for m in mods do
        match m with
        | SLICE(s) ->
            if s.left.relTo = s.right.relTo && s.left.x > s.right.x then
                // TODO: better to report coordinates of slice text rather than gene
                failwithf "ERROR: slice left %A greater than right %A %s in %s\n"
                    s.left.x s.right.x (fp where) errorRef
        | _ -> () // No checks for now TODO

/// Given a feature and a relative position, find the zero based physical
/// coordinate for the position.
/// Determine range of DNA needed, translating into physical coordinates
/// Final start of the piece.  Determine which end we are working relative to
let adjustToPhysical (feat:sgd.Feature) (f:RelPos) =
    (match f.relTo with
     | FIVEPRIME ->  if feat.fwd then feat.l*1<ZeroOffset> else feat.r*1<ZeroOffset>
     | THREEPRIME -> if feat.fwd then feat.r*1<ZeroOffset> else feat.l*1<ZeroOffset>
    ) +
    // Determine which direction to move in absolute coords depending on gene strand
    (if feat.fwd then (1) else (-1)) *
    // Determine offset, allowing for the -1/1 based coordinate system with no zero
    (one2ZeroOffset f)


/// Generate logical coordinates for the start and end of the gene part
/// these are relative to the gene, not the genome for now.  We transform
/// to genomic coordinates below.
/// Transform all non gXXX forms of gene into gXX forms.
let translateGenePrefix (gd : GenomeDef) (gPart : StandardSlice) =
    match gPart with
    | PROMOTER ->
        {left = {x = -500<OneOffset>; relTo = FIVEPRIME};
         lApprox = true;
         rApprox = false;
         right = { x = -1<OneOffset>; relTo = FIVEPRIME } }
    | UPSTREAM ->
        {left = {x = -gd.getFlank(); relTo = FIVEPRIME};
         lApprox = true;
         rApprox = false;
         right = { x = -1<OneOffset>; relTo = FIVEPRIME } }
    | TERMINATOR ->
        {left = {x = 1<OneOffset>; relTo = THREEPRIME};
         lApprox = false;
         rApprox = true;
         right = { x = 500<OneOffset>; relTo = THREEPRIME } }
    | DOWNSTREAM ->
        {left = {x = 1<OneOffset>; relTo = THREEPRIME};
         lApprox = false;
         rApprox = true;
         right = { x = gd.getFlank(); relTo = THREEPRIME } }
    | FUSABLEORF ->
        {left = {x = 1<OneOffset>; relTo = FIVEPRIME};
         lApprox = false;
         rApprox = false;
         right = { x = -4<OneOffset>; relTo = THREEPRIME } }
    | ORF ->
        {left = {x = 1<OneOffset>; relTo = FIVEPRIME};
         lApprox = false;
         rApprox = false;
         right = { x = -1<OneOffset>; relTo = THREEPRIME } }
    | GENE ->
        {left = {x = 1<OneOffset>; relTo = FIVEPRIME};
         lApprox = false;
         rApprox = false;
         right = { x = -1<OneOffset>; relTo = THREEPRIME } }
    | MRNA ->
        {left = {x = 1<OneOffset>; relTo = FIVEPRIME};
         lApprox = false;
         rApprox = true;
         right = { x = 200<OneOffset> ; relTo = THREEPRIME } }


/// Translate gene part label.  Raises an exception for errors.
let lookupGenePart errorDescription prefix (modList:Mod list) =
    let sliceType =
        match charToSliceType prefix with
        | Some(t) -> t
        | None ->
            failwithf
                "ERROR: unknown gene prefix '%c' should be one of %s in %s"
                prefix (sliceTypeChars.ToString()) errorDescription

    let dotModList =
        seq {
            for m in modList do
                match m with
                | DOTMOD(dm) -> yield dm
                | _ -> ()}
        |> List.ofSeq

    match dotModList with
    | [] -> sliceType
    | [dm] ->
        // if it's a dot mod, it must be on a 'g' part (gYNG2) (For now...)
        if sliceType <> GENE then
            failwithf
                "ERROR: cannot apply DOTMODS to non-gene parts (must be gXYZ). Used %c"
                prefix

        match dm.i with
        | "up" -> UPSTREAM
        | "down" -> DOWNSTREAM
        | "mrna" -> MRNA
        | x ->
            failwithf
                "ERROR: unimplemented DOTMOD %s. Options: up, down, mrna"
                x
    | _ -> failwithf "ERROR: multiple DOTMODS applied to %s" errorDescription


/// Get the reference genome for a given assembly and set of pragmas.
/// Exception on error.
let getRG (a:Assembly) (rgs:GenomeDefs) (pr:PragmaCollection) =
    // First check if the pragmas have defined a reference genome.
    let rgName =
        match pr.TryGetOne("refgenome") with
        | Some(rg) -> rg
        | None -> // try to fall back to the assembly
            match a.pragmas.TryGetOne("refgenome") with
            | Some(rg) -> rg
            | None -> defaultRefGenome
    match rgs.TryFind(rgName) with
    | Some(g) -> g
    | None ->
        if rgName = defaultRefGenome then
            failwithf
                "ERROR: unable to load default genome '%s' <currently loaded: %s>"
                defaultRefGenome
                (if rgs.Count=0 then "none"
                 else String.Join (",",[ for k in rgs -> k.Key]))
        else
            failwithf
                "ERROR: no such refgenome '%s', options are\n%s"
                rgName
                (String.Join("\n",seq { for k in rgs -> sprintf "    '%s'" k.Key }))

/// Take a genepart and slices and get the actual DNA sequence.
let realizeSequence verbose fwd (rg:GenomeDef) (gp:GenePartWithLinker) =

    if verbose then
        printf "realizeSequence:  fetch fwd=%s %s\n"
            (if fwd then "y" else "n") gp.part.gene

    // Inspect prefix of gene e.g g,t,o,p and see what type of gene part we are starting with
    // Description of part to give in case of error
    let errorDesc = gp.part.gene
    let genePart = lookupGenePart errorDesc (gp.part.gene.[0]) (gp.part.mods)

    // Lookup gene location
    let feat = rg.get(gp.part.gene.[1..])

    // Come up with an initial slice based on the gene prefix type
    let s = translateGenePrefix rg genePart

    let finalSlice = applySlices verbose gp.part.mods s
    let left = adjustToPhysical feat finalSlice.left
    let right = adjustToPhysical feat finalSlice.right

    // One final adjustment needed.  We have defined left and right relative to
    // the gene, but if the gene is on the crick strand, we need to both flip
    // the coordinates and reverse complement the resulting DNA to keep it
    // oriented with the construction orientation.
    if (left > right && feat.fwd) || (right>left && (not feat.fwd)) then
        failwithf
            "ERROR: [realizeSequence] slice results in negatively lengthed DNA piece gene=%s slice=%s\n"
            feat.gene (printSlice finalSlice)

    let left', right' = if feat.fwd then left, right else right, left
    rg.Dna(errorDesc, sprintf "%d" feat.chr, left', right')
        |> (if feat.fwd then id else revComp)
        |> (if fwd then id else revComp)


/// Extract slice name from a PPP, if it has one.
let getSliceName (ppp:PPP) =
    match ppp.pr.TryGetOne("name") with
    | Some(name) -> name
    | None -> ""

/// Extract URI from a PPP, if it has one.
let getUri (ppp:PPP) = ppp.pr.TryGetOne("uri")

/// Expand a marker part into DNA pieces.
/// Exception on failure.
let expandMarkerPart
    (library:Map<string,char array>)
    dnaSource
    (ppp:PPP) =

    if not (library.ContainsKey("URA3")) then
        failwithf "ERROR: lib.fa does not contain URA3 marker part\n"

    let dna = library.["URA3"]
    {id = None;
     extId = None;
     sliceName = getSliceName ppp;
     uri = getUri ppp; // TODO: should this marker have a static URI we always assign here?
     dna = dna;
     sourceChr = "library";
     sourceFr = 0<ZeroOffset>;
     sourceTo = (dna.Length-1)*1<ZeroOffset>;
     sourceFwd = true;
     sourceFrApprox = false;
     sourceToApprox = false;
     // Don't assign coordinates to pieces until later when we
     // decide how they are getting joined up
     template = Some dna;
     amplified = false;
     destFr = 0<ZeroOffset>;
     destTo = 0<ZeroOffset>;
     destFwd = ppp.fwd;
     description = "URA3 marker";
     sliceType = MARKER;
     dnaSource = dnaSource;
     pragmas = ppp.pr;
     breed = B_MARKER;
     // Added "rabitCandidates"-----8/12/2015, Liang Mi (Darren Platt)
     rabitCandidates = Array.empty;}

let expandInlineDna
    dnaSource
    (ppp:PPP)
    (dna:string) =

    let dnaArr = dna.ToCharArray() |> (if ppp.fwd then (id) else (revComp))

    {id = None;
     extId = None;
     sliceName = getSliceName ppp;
     uri = getUri ppp;
     dna = dnaArr;
     sourceChr = "inline";
     sourceFr = 0<ZeroOffset>;
     sourceTo = (dna.Length-1)*1<ZeroOffset>;
     sourceFwd = true;
     sourceFrApprox = false;
     sourceToApprox = false;
     // NB - for now allow that this might be amplified, but could change later
     template = Some dnaArr;
     amplified = false;
     // Don't assign coordinates to pieces until later when we decide how they are getting joined up
     destFr = 0<ZeroOffset>;
     destTo = 0<ZeroOffset>;
     destFwd = ppp.fwd;
     description = (if ppp.fwd then dna else "!"+dna );
     sliceType = INLINEST;
     dnaSource = dnaSource;
     pragmas = ppp.pr;
     breed = B_INLINE;
     // Added "rabitCandidates"-----8/12/2015, Liang Mi (Darren Platt)
     rabitCandidates = Array.empty;}

let expandGenePart
    verbose
    (rgs:GenomeDefs)
    (library:Map<string,char array>)
    (a:Assembly)
    (proxyURL:string option)
    dnaSource
    (ppp:PPP)
    (gp:GenePartWithLinker) =

    match gp.linker with
    | None -> () // No linkers were present
    | Some(l) -> checkLinker l // Test the linkers

    // Check the genes are legal
    //let prefix = gp.part.gene.[0]
    let g = gp.part.gene.[1..].ToUpper()
    let rg' = getRG a rgs ppp.pr

    if not (rg'.IsValid(g)) then
        // Not a genomic reference but might still be in our library
        if library.ContainsKey(g) then
            // Yes! =- make up a little island of sequence for it
            let dna = library.[g]

            // Need to adjust for any slicing carefully since the DNA island is small
            // Validate mods to gene
            let errorRef = match a.name with | None -> sprintf "%A" a | Some(x) -> x
            validateMods errorRef gp.part.where gp.part.mods

            // Come up with an initial slice based on the gene prefix type

            // Get standard slice range for a gene
            let s = translateGenePrefix rg' GENE
            let finalSlice = applySlices verbose gp.part.mods s

            // Ban approx slices to stay sane for now
            if finalSlice.lApprox || finalSlice.rApprox then
                failwithf
                    "ERROR: sorry, approximate slices of library genes not supported yet in %s\n"
                    (ASSEMBLY(a) |> prettyPrintLine)

            let x =
                match finalSlice.left.relTo with
                | FIVEPRIME -> finalSlice.left.x
                | THREEPRIME -> (dna.Length+1)*1<OneOffset> + finalSlice.left.x
            let y =
                match finalSlice.right.relTo with
                | FIVEPRIME -> finalSlice.right.x
                | THREEPRIME -> (dna.Length+1)*1<OneOffset> + finalSlice.right.x

            if x < 1<OneOffset> || y <=x || y > (dna.Length*1<OneOffset>) then
                failwithf
                    "ERROR: illegal slice (%A) outside core gene range for library gene %s\n"
                    finalSlice gp.part.gene

            let finalDNA =
                dna.[(x/1<OneOffset>)-1..(y/1<OneOffset>)-1]
                |> (if ppp.fwd then (id) else (revComp))

            let name1 =
                if gp.part.mods.Length = 0 then gp.part.gene
                else (gp.part.gene + (printSlice finalSlice))
            let name2 = if ppp.fwd then name1 else "!"+name1

            {id = None;
             extId = None;
             sliceName = getSliceName ppp;
             uri = getUri ppp; // TODO: should we also check a returned library part for a URI?
             dna = finalDNA;
             sourceChr = "library";
             sourceFr = (finalSlice.left.x/(1<OneOffset>)-1)*1<ZeroOffset>;
             sourceTo = (finalSlice.right.x/(1<OneOffset>)-1)*1<ZeroOffset>;
             sourceFwd = true;
             sourceFrApprox = false;
             sourceToApprox = false;
             amplified = false;
             // This is what we are expecting to amplify from (library part)
             template = Some finalDNA;
             // Don't assign coordinates to pieces until later when we decide how they are getting joined up
             destFr = 0<ZeroOffset>;
             destTo = 0<ZeroOffset>;
             destFwd = ppp.fwd;
             description = name2;
             sliceType = REGULAR;
             dnaSource = dnaSource;
             pragmas = ppp.pr;
             breed = B_X;
             rabitCandidates = Array.empty;}
        else // no :( - wasn't in genome or library
            failwithf "ERROR: undefined gene '%s' %s\n" g (fp gp.part.where)
    else
        // Inspect prefix of gene e.g g,t,o,p and see what type of gene part we are starting with
        let errorDesc = gp.part.gene
        let genePart = lookupGenePart errorDesc (gp.part.gene.[0]) (gp.part.mods)
        // Lookup gene location
        let rg' = getRG a rgs ppp.pr

        let feat = rg'.get(g)

        let breed1 =
            match genePart with
            | PROMOTER -> B_PROMOTER
            | TERMINATOR -> B_TERMINATOR
            | MRNA -> B_GST
            | DOWNSTREAM -> B_DOWNSTREAM
            | UPSTREAM -> B_UPSTREAM
            | FUSABLEORF -> B_FUSABLEORF
            | GENE -> B_X
            | ORF -> B_GS

        // WARNING - very similar logic in realizeSequence and both versions
        // should be considered when changing logic.
        // FIXME: this common logic should be refactored into a generic function
        // and called in both places.

        // Validate mods to gene
        let errorRef = match a.name with | None -> sprintf "%A" a | Some(x) -> x

        validateMods errorRef gp.part.where gp.part.mods
        // Come up with an initial slice based on the gene prefix type
        let s = translateGenePrefix rg' genePart
        if verbose then printf "log: processing %A\n" a

        // finalSlice is the consolidated gene relative coordinate of desired piece
        let finalSlice = applySlices verbose gp.part.mods s

        // Calculate some adjusted boundaries in case the left/right edges are approximate
        let leftAdj =
            {finalSlice.left with x = finalSlice.left.x-(approxMargin * 1<OneOffset>)}
        let rightAdj =
            {finalSlice.right with x = finalSlice.right.x+(approxMargin * 1<OneOffset>)}

        // Gene relative coordinates for the gene slice we want
        let finalSliceWithApprox =
           {lApprox = finalSlice.lApprox;
            left = (if finalSlice.lApprox then leftAdj else finalSlice.left);
            rApprox = finalSlice.rApprox;
            right = if finalSlice.rApprox then rightAdj else finalSlice.right}

        if verbose then
            printf "log: finalSlice: %s%s %s%s\n"
                (if finalSlice.lApprox then "~" else "")
                (printRP finalSlice.left)
                (if finalSlice.rApprox then "~" else "")
                (printRP finalSlice.right)
        if verbose then
            printf "log: finalSliceWA: %s%s %s%s\n"
                (if finalSliceWithApprox.lApprox then "~" else "")
                (printRP finalSliceWithApprox.left)
                (if finalSliceWithApprox.rApprox then "~" else "")
                (printRP finalSliceWithApprox.right)

        // FinalSliceWithApprox is a gene relative coordinate system, but we
        // need genomic coordinates for the gene

        // Left is the genomic left end of the element
        let left = adjustToPhysical feat finalSliceWithApprox.left
        // Right is the genomic right end of the element
        let right = adjustToPhysical feat finalSliceWithApprox.right

        if verbose then
            printf "log: gene: %s %d %d %s\n"
                feat.gene feat.l feat.r (if feat.fwd then "fwd" else "rev")
            printf "log: prefinal: %s %A %A\n" feat.gene left right

        // One final adjustment needed.  We have defined left and right relative
        // to the gene, but if the gene is on the crick strand, we need to both
        // flip the coordinates and reverse complement the resulting DNA to keep
        // it oriented with the construction orientation.

        if (left > right && feat.fwd) || (right>left && (not feat.fwd)) then
            failwithf
                "ERROR: slice results in negatively lengthed DNA piece for %s\n"
                (gp.part.gene + (printSlice finalSlice))

        /// left' is the genomic coordinate of the start of the element (i.e gene upstream)
        let left', right' = if feat.fwd then left, right else right, left
        if verbose then printf "log: final: %s %A %A\n" feat.gene left' right'

        // TOO BLUNT gp.part.gene = "gURA3"
        // TODO: hard coded detection of split marker
        let isMarker = false
        let rg' = getRG a rgs ppp.pr

        if verbose then
            printf "gettingdna for %s fwd=%s\n"
                feat.gene (if ppp.fwd then "y" else "n")

        let dna =
            rg'.Dna(errorDesc,sprintf "%d" feat.chr,left',right')
            |> (if feat.fwd then id else revComp)
            // One potential final flip if user wants DNA backwards
            |> (if ppp.fwd then id else revComp)

        let description1 =
            match gp.part.mods with
            | [] -> gp.part.gene
            | [DOTMOD(d)] ->
                match d.i with
                | "up" -> "u" + gp.part.gene.[1..]
                | "down" -> "d" + gp.part.gene.[1..]
                | "mrna" -> "m" + gp.part.gene.[1..]
                | x -> failwithf "ERROR: unimplemented DOTMOD %s" x
            | _ -> "g" + gp.part.gene.[1..] + (printSlice finalSlice)
        let description2 = if ppp.fwd then description1 else "!"+description1

        let promStart = {x = -300<OneOffset>; relTo = FIVEPRIME}
        let promEnd = {x = -1<OneOffset>; relTo = FIVEPRIME}
        let termStart = {x = 1<OneOffset>; relTo = THREEPRIME}
        let termEnd = {x = 150<OneOffset>; relTo = THREEPRIME}

        let near (a:RelPos) (b:RelPos) (tolerance) =
            a.relTo = b.relTo && abs((a.x-b.x)/1<OneOffset>)< tolerance

        let breed =
            match breed1 with
            | B_X ->
                let z = finalSliceWithApprox
                if near z.left termStart 1 && near z.right termEnd 100 then
                    B_TERMINATOR
                elif near z.left promStart 400 && near z.right promEnd 40 then
                    B_PROMOTER
                elif z.left.x=1<OneOffset> &&
                     z.left.relTo=FIVEPRIME &&
                     near z.right termEnd 100
                    then B_GST
                else B_X
            | x -> x

        // flag_new_gsl 8/12/15 --- top (paired with bottom)
        let rabitCandidates =
            match proxyURL with
                | Some(url) ->
                    let breedThumper, insertName =
                        match breed with
                        | B_UPSTREAM -> "U", "US_"+g
                        | B_DOWNSTREAM -> "D", "DS_"+g
                        // Todo: Add more breed code
                        | _ -> "?","??"

                    if verbose then
                        printf "--> Searching candidate rabits with name=%s and breed=%s\n"
                            insertName breedThumper

                    let rabitLookupResults = fetchProxy url insertName breedThumper
                    if verbose then printf "--> All candidate rabits:\n%A\n" rabitLookupResults
                    rabitLookupResults
                | None ->
                    Array.empty
        // flag_new_gsl 8/12/15 --- bottom (paired with top)

        // Note regarding orientation: We are currently building a single piece
        // of final DNA left to right. There is no consideration for stitch
        // orientation, so even (in RYSEworld) B stitch parts are laid out left
        // to right from marker (innermost 9 linker) through to the outler linker.
        // If something is reversed then, it points towards the middle, marker
        // part of the stitch.
        {id = None;
         extId = None;
         sliceName = getSliceName ppp;
         uri = getUri ppp;
         dna = dna;
         sourceChr = feat.chr |> string;
         sourceFr = (if ppp.fwd then left' else right'); // 5' end of gene element in the genome
         sourceTo = (if ppp.fwd then right' else left'); // 3' end of gene element in the genome
         sourceFwd = feat.fwd;
         amplified = true;
         template = Some dna; // This is what we are expecting to amplify from genome
         // Don't assign coordinates to pieces until later when we decide how
         // they are getting joined up. Left and Right are absolute genomic
         // coordinate relative (i.e left has a smaller coordinate than left)

         // This logic is a bit twisted. SourceFrApprox is misleading, this
         // designates whether the *left* end is approx or now and that operation
         // occurs before the orientation of the rabit is applied, so
         // !gABC1[100:~200E] has a sourceFrApprox = false initially.  We flipped
         // the actual piece of DNA so the l and r approx need to move with the DNA
         sourceFrApprox = (if ppp.fwd then finalSlice.lApprox else finalSlice.rApprox);
         sourceToApprox = (if ppp.fwd then finalSlice.rApprox else finalSlice.lApprox);
         destFr = 0<ZeroOffset>;
         destTo = 0<ZeroOffset>;
         destFwd = ppp.fwd;
         description = description2;
         dnaSource = dnaSource;
         sliceType = (if isMarker then MARKER else REGULAR);
         pragmas = ppp.pr;
         breed = breed;
         rabitCandidates = rabitCandidates}

///
/// Take a PPP that is of type multipart and turn it into a simple
/// list of underlying PPPs, applying any reverse operator and
/// disributing high level pragmas over underlying PPPs
let prepPPPMultiPart _(*fws*) _(*pr*) (parts:PPP list) =
        parts // No nothing for now

/// Take a parsed assembly definition and translate it
/// to underlying DNA pieces, checking the structure in the process.
/// Raises an exception on error.
let expandAssembly
    verbose
    (rgs:GenomeDefs)
    (library:Map<string,char array>)
    (a:Assembly)
    (proxyURL:string option) =

    let rec expandPPPList pppList =
        seq {
            // NOTE: have access to part.pragmas to the extent they influence generation
            for ppp in pppList do
                //let sliceName = getSliceName ppp

                let dnaSource =
                    match ppp.pr.TryGetOne("dnasrc") with
                    | Some(d) -> d
                    | None ->
                        // specifying a different reference genome implies a non standard
                        // DNA source, so we can use that too (they can override with dnasrc)
                        match ppp.pr.TryGetOne("refgenome") with
                        | Some (rg) -> rg
                        | None ->
                            match a.pragmas.TryGetOne("refgenome") with
                            | Some(rg) -> rg
                            | None -> "" // Revert to the current default part origin

                match ppp.part with
                | ERRORPART(pe) ->
                    failwithf "ERROR: %s @%d,%d" pe.message pe.s.Line (pe.s.Column+1)
                | MARKERPART ->
                    yield expandMarkerPart library dnaSource ppp
                | PARTID(partId) ->
                    yield resolveExtPart.fetchSequence verbose library ppp partId
                | INLINEDNA(dna) ->
                    yield expandInlineDna dnaSource ppp dna
                | INLINEPROT(_) ->
                    failwith "ERROR: unexpanded protein inline encountered during DNA generation"
                | HETBLOCK ->
                    failwith "ERROR: unexpanded heterology block encountered during DNA generation"
                | EXPANDED(_) -> ()
                | MULTIPART(pppList) ->
                        // Some logic to apply here.  We are going to expand a multipart
                        // that contains a list of underlying PPPs.  At a simple level we can just expand
                        // the PPP list but user might have also applied instructions to the top level
                        // multipart in form of pragmas or directionality, so we have to take care of
                        // those before doing the expansion with prepPPPMultiPart then recursively expand
                        yield! prepPPPMultiPart ppp.fwd ppp.pr pppList |> expandPPPList
                | GENEPART(gp) ->
                    yield expandGenePart
                        verbose rgs library a proxyURL dnaSource ppp gp
                //
                // Might also want to yield a fusion slice
                //
                if ppp.pr.ContainsKey("fuse") then
                    yield
                       {id = None;
                        extId = None;
                        sliceName = "fusion";
                        uri = None; // TODO: uri for fusion parts?
                        dna = [||];
                        sourceChr = "";
                        sourceFr = 0<ZeroOffset>;
                        sourceTo = 0<ZeroOffset>;
                        sourceFwd = true;
                        template = None;
                        amplified = false;
                        sourceFrApprox = false;
                        sourceToApprox =false;
                        destFr = 0<ZeroOffset>;
                        destTo = 0<ZeroOffset>;
                        destFwd= true;
                        description ="::";
                        dnaSource = "";
                        sliceType = FUSIONST;
                        pragmas = EmptyPragmas;
                        breed = B_VIRTUAL;
                        // Added "rabitCandidates"-----8/12/2015, Liang Mi (Darren Platt)
                        rabitCandidates = Array.empty;
                       }
            } |> List.ofSeq |> recalcOffset
    expandPPPList a.parts
