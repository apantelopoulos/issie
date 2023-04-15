﻿module BusWireSeparate
open CommonTypes
open Elmish
open DrawHelpers
open DrawModelType.SymbolT
open DrawModelType.BusWireT
open BusWire
open BusWireUpdateHelpers
open SmartHelpers

open Optics
open Operators

//*****************************************************************************************************//
//---------------------------------Smart Channel / Segment Order / Separate----------------------------//
//*****************************************************************************************************//

(*-----------------------------------------------------------------------------------------------------
    this code implements a sheet beautify function that is designed to be called at the end of a symbol drag, 
    wire creation, etc, after smart autoroute. Therefore it has time to analyse the whole circuit and make changes. 
 
    Currently implements:
    - spread out overlapping wire segments
    - order wire segments to minimise crossings
    - order wire segments to minimise overlaps
    - allow same-net segments to overlap

    Does not implement:
    - re-order ports on custom components, or flip other components. That would be an obvious and quite easy 
    extension.
    *)

open SmartHelpers.Constants // for easy access to SmartWire Constant definitions

//-------------------------------------------------------------------------------------------------//
//---------------------------------LINE ARRAY CREATION FROM MODEL----------------------------------//
//-------------------------------------------------------------------------------------------------//

/// return wire and segment index of line, if it is a segment
let lineToWire (model: Model) (line:Line)  : (Wire * int) option =
    match line.Seg1 with
    | Some seg ->
        let (int,wid) = seg.Segment.GetId()
        let wire = model.Wires[wid]
        Some (wire,int)
    | None -> None
        

/// Convert a segment into a fixed or movable line (of given orientation).
let segmentToLine (lType: LType) (ori: Orientation) (wire:Wire) (seg: ASegment) : Line =
    let order a b =
        if a < b then
            { MinB = a; MaxB = b }
        else
            { MinB = b; MaxB = a }

    let line: Line =
        {   P = seg.Start.Y
            Orientation = ori
            B = order seg.Start.X seg.End.X
            LType = lType
            Seg1 = Some seg
            SameNetLink = []
            Wid = wire.WId
            PortId = wire.OutputPort
            Lid = LineId 0 }

    match ori with
    | Horizontal ->
        line
    | Vertical ->
        {line with 
            P = seg.Start.X; 
            B = order seg.Start.Y seg.End.Y}

/// Convert a symbol BoundingBox into two fixed lines (of given orientation).
let bBoxToLines (ori: Orientation) (box: BoundingBox) : Line list =
    let tl = box.TopLeft

    match ori with
    | Horizontal -> [ tl.Y, tl.X, tl.X + box.W; tl.Y + box.H, tl.X, tl.X + box.W ]
    | Vertical -> [ tl.X, tl.Y, tl.Y + box.H; tl.X + box.W, tl.Y, tl.Y + box.H ]
    |> List.map (fun (p, minB, maxB) ->
        {   P = p
            B =
              { MinB = minB + smallOffset
                MaxB = maxB - smallOffset }
            Orientation = ori
            LType = FIXED
            Seg1 = None
            SameNetLink = []
            Wid = ConnectionId ""
            PortId = OutputPortId ""
            Lid = LineId 0 })

/// Where two segments are on the same Net and on top of each other we muct NEVER separate them.
/// This function links such segments, and marks all except the head one with ClusterSegment = false, 
/// so that the clustering algorithm will ignore them.
let linkSameNetLines (lines: Line list) : Line list =
    /// input: list of lines all in the same Net (same outputPort)
    /// output: similar list, with lines that are on top of each other and in different wires linked
    let linkSameNetGroup (lines: Line list) =
        let lines = List.toArray lines
        /// if needed, link lines[b] to lines[a] mutating elements in lines array for efficiency
        let tryToLink (a:int) (b:int) =
            let la, lb = lines[a], lines[b]
            if la.LType = NORMSEG && la.Wid <> lb.Wid && close la.P lb.P && hasOverlap la.B lb.B  then
                lines[b] <- 
                    { lb with
                        LType = LINKEDSEG}
                lines[a] <- 
                    { la with
                        B = boundUnion la.B lb.B;
                        SameNetLink = lines[b] :: lines[a].SameNetLink}                    
        // in this loop the first lines[a] in each linkable set links all the set, setting ClusterSegment = false
        // Linked lines are then skipped.
        for a in [0..lines.Length-1] do
            for b in [a+1..lines.Length-1] do
                tryToLink a b
        Array.toList lines

    lines
    |> List.groupBy (fun line -> line.PortId)
    |> List.collect (fun (port, lines) -> linkSameNetGroup lines)

/// Make all lines, fixed and movable, of given orientation from wires and symbols in Model
/// ori - orientation of Lines (P coord is reverse of this)
let makeLines (ori: Orientation) (model: Model) =
    /// Which segments in wires are included as Lines?
    let selectSegments (ori: Orientation) (wire: Wire) (orient: Orientation) (seg: Segment) =
        let numSegs = wire.Segments.Length
        ori = orient && seg.Index <> 0 && seg.Index <> numSegs - 1 && not (seg.IsZero()) //|| (segN -1).IsZero() || (segN 1).IsZero())

    /// Lines coming from wire segments
    /// Manually routed segments are considered fixed
    /// Segments next to zero length segments are considered fixed
    /// (they form part of straight lines extending the fixed nub)
    let segLines =
        ([], model.Wires)
        ||> Map.fold (fun (lines: Line list) _ wire ->
            getFilteredAbsSegments (selectSegments ori wire) wire
            |> List.map (fun aSeg ->
                let segs = wire.Segments
                let seg = aSeg.Segment
                let lType =
                    match seg.Mode, seg.Index=2, seg.Index=segs.Length-3 with
                    | Manual , _ , _ -> 
                        FIXEDMANUALSEG
                    | _ , true , _ when segs[ 1 ].IsZero() -> 
                        FIXEDSEG
                    | _ , _ , true when  segs[ segs.Length - 2 ].IsZero() -> 
                        FIXEDSEG
                    | _ -> 
                        NORMSEG
                segmentToLine lType ori wire aSeg)
            |> (fun wireLines -> wireLines @ lines))
            |> linkSameNetLines




    /// Lines coming from the bounding boxes of symbols
    let symLines =
        model.Symbol.Symbols
        |> Map.toList
        |> List.collect (fun (_, sym) -> Symbol.getSymbolBoundingBox sym |> bBoxToLines ori)

    symLines @ segLines
    |> List.toArray
    |> Array.sortBy (fun line -> line.P)
    |> Array.mapi (fun i line -> { line with Lid = LineId i })
    //|>  (fun arr -> printf "%s" (pLines arr); arr)

//-------------------------------------------------------------------------------------------------//
//-----------------------------------------SEGMENT ORDERING----------------------------------------//
//-------------------------------------------------------------------------------------------------//


/// Returns integers +/- 1 indicating direction of wire leaving ends of line segment.
/// Pair returned is MaxB, MinB end of line
let turnDirs (line: Line) (wires: Map<ConnectionId, Wire>) =
    match line.Seg1 with
    | None -> failwithf "What? Expected Some segment - not None"
    | Some aSeg ->
        let seg = aSeg.Segment
        let wSegs = wires[seg.WireId].Segments
        // segment length is + or - according to whether segment.P end is larger or samller than start.
        let segLength segIndex = wSegs[segIndex].Length
        // len1, len2 is P coordinate (P = X or Y) change from the line segment at MaxB, MinB end of line.
        // the seg.Index-1 end has change inverted because its change is from, not to line.
        let len1, len2 =
            if seg.Length > 0 then
                segLength (seg.Index + 1), - segLength(seg.Index - 1)
            else
                - segLength(seg.Index - 1), segLength (seg.Index + 1)

        sign len1, sign len2

// The functions tests two segment ends - one from each wire - for whether the
// segments connected to the ends (and therefore turning one direction or the other)
// might overlap.
/// Return +1. if two wires turn towards each other (and therefore might overlap), else -1.
/// turnDir1, turnDir2 - direction in which turns go.
/// bound1, bound2 - the MinB or MaxB bound of each end - which must be close.
/// The P value of each segment.
let linesMaybeMeeting
    ((turnDir1, bound1, p1): int * float * float)
    ((turnDir2, bound2, p2): int * float * float)
    : float =
    // if the two segment ends do not line up return 0.
    // and the two segments that join turn towards eachother
    match close bound1 bound2,  p1 > p2, turnDir1, turnDir2 with
    | false, _, _, _ -> 0.
    | _, true, -1, 1
    | _, false, 1, -1 -> 1.
    | _ -> -1.


/// +1 if line1.P > line2.P for zero crossings.
/// -1 if line1.P < line2.P for zero crossings.
/// 0 if line1.P and line2.P have one crossing.
let numCrossingsSignAndMaybeOverlaps (line1: Line) (line2: Line) (wires: Map<ConnectionId, Wire>) =
    let (max1, min1), (max2, min2) = turnDirs line1 wires, turnDirs line2 wires
    // if line1.P > line2.P then a +1 line1 turnDir or a -1 line2 turnDir from an inner endpoint
    // will NOT cause a crossing. -1 will cause a crossing.
    // The match sums the two inner turnDirs, inverting sign if they come from a line2
    // turning. Dividing this by 2 gives the required answer!
    // NB this is simpler than expected since it does not matter what order the two inner ends are
    // in - which makes identifying them (as one of the MaxB and one of the MinB ends) easier.
    let crossingsNumSign =
        match line1.B.MinB > line2.B.MinB, line1.B.MaxB < line2.B.MaxB with
        | true, true -> min1 + max1
        | true, false -> min1 - max2
        | false, true -> -min2 + max1
        | false, false -> -min2 + max2
        |> (fun n -> n / 2)
    // if two segment ends have the same Bound (MaxB or MinB) value and turn towards each other
    // still experimental (the negative weighting of this perhaps means it should be the otehr way round)?
    let maybeMeeting =
        linesMaybeMeeting (max1, line1.B.MaxB, line1.P) (max2, line2.B.MaxB, line2.P)
        + linesMaybeMeeting (max1, line1.B.MaxB, line1.P) (min2, line2.B.MinB, line2.P)
        + linesMaybeMeeting (min1, line1.B.MinB, line1.P) (max2, line2.B.MaxB, line2.P)
        + linesMaybeMeeting (min1, line1.B.MinB, line1.P) (min2, line2.B.MinB, line2.P)

    //printfn $"line1 = {line1.Index}, line2 = {line2.Index}. MaybeMeeting = {maybeMeeting}"
    float crossingsNumSign + maybeMeeting 

/// segL is a list of lines array indexes representing segments found close together.
/// Return the list ordered in such a way that wire crossings are minimised if the
/// segments are placed as ordered. The return list is placed with P value increasing
/// along the list.
let orderToMinimiseCrossings (model: Model) (lines: Line array) (segL: int list) =
    // special case - included for efficency
    if segL.Length = 1 then
        segL
    else
        let wires = model.Wires
        let numSegments = segL.Length
        let segA = segL |> Array.ofList
        /// inverse of segA[index]: NB indexes [0..numSegmnets-1] are NOT Segment index.
        /// These indexes are used inside this function only to allow contiguous arrays
        /// to calculate the sort order
        let indexOf seg = Array.findIndex ((=) seg) segA
        // Map each index [0..numSegments-1] to a number that will determine its (optimal) ordering
        let sortOrderA =
            let arr = Array.create numSegments 0.

            for i in [ 0 .. numSegments - 1 ] do
                for j in [ 0 .. i - 1 ] do
                    let num = numCrossingsSignAndMaybeOverlaps lines[segL[i]] lines[segL[j]] wires
                    arr[i] <- arr[i] + num
                    arr[j] <- arr[j] - num

            arr

        let sortFun i = sortOrderA[indexOf i]
        List.sortBy sortFun segL

//-------------------------------------------------------------------------------------------------//
//---------------------------------------SEGMENT CLUSTERING----------------------------------------//
//-------------------------------------------------------------------------------------------------//

/// When given a segment index search for nearby segments to be considered with it as a single cluster
/// for spreading out. To be included segments must be close enough and overlapping. Search
/// terminates given large gap or a fixed boundary segments are not allowed to move across.
let expandCluster (groupableA: bool array) (index: int) (searchDir: LocSearchDir) (lines: Line array) =
    let nextIndex i =
        match searchDir with
        | Upwards -> i + 1
        | _ -> i - 1

    let searchStart = lines[index].P

    let initLoc, lowestDownwardsIndex =
        match searchDir with
        | Upwards ->
              { UpperFix = None
                LowerFix = None
                Bound = lines[index].B
                Segments = [ index ] },
              None
        | Downwards loc ->
            let index = List.max loc.Segments
            { loc with Segments = [ index ] }, Some(List.min loc.Segments)

    let rec expand i loc =
        let nSegs = float loc.Segments.Length
        //printf $"""Expanding: {if i >= 0 && float i < lines.Length then  pLine lines[i] else "OOB"} {pCluster loc}"""

        if (i < 0 || i >= lines.Length) || abs (lines[i].P - searchStart) > maxSegmentSeparation * (nSegs+1.) + smallOffset then
            //if i >= 0 && i < lines.Length then 
                //printf $"Gapped:{abs (lines[i].P - searchStart)} {maxSegmentSeparation * nSegs + smallOffset} "
            loc
        elif not  (hasOverlap loc.Bound lines[i].B) then
            expand (nextIndex i) loc
        else
            let p = lines[i].P
            match lines[i].LType with
            | FIXED | FIXEDMANUALSEG | FIXEDSEG ->
                let p = lines[i].P
                match searchDir with
                | Upwards -> { loc with UpperFix = Some p }
                | _ -> { loc with LowerFix = Some p } // fixed boundary 
            | LINKEDSEG ->
                expand (nextIndex i) loc

            | NORMSEG ->
                match lowestDownwardsIndex with
                | Some index when i < index -> expand (nextIndex i) loc // past starting point, so can't add segments, but still check for a Fix
                | _ ->
                    expand
                        (nextIndex i)
                        { loc with
                            Segments = i :: loc.Segments
                            Bound = boundUnion loc.Bound lines[i].B }

    expand (nextIndex index) initLoc

/// Scan through segments in P order creating a list of local Clusters.
/// Within one cluster segments are adjacent and overlapping. Note that
/// different clusters may occupy the same P values if their segments do
/// not overlap.
/// Segments within each cluster will be repositioned and reordered after
/// clusters are identified.
/// Every segment must be part of a unique cluster.
let makeClusters (lines: Line array) =
    /// true if corresponding line can be grouped in a cluster as a segment
    let groupableA =
        Array.init lines.Length (fun i ->lines[i].LType = NORMSEG)

    let groupable seg = groupableA[seg]
    let expandCluster = expandCluster groupableA

    let keepOnlyGroupableSegments (loc: Cluster) =
        { loc with Segments = List.filter groupable loc.Segments }

    let rec getClusters lines =
        match Array.tryFindIndex ((=) true) groupableA with
        | None -> []
        | Some nextIndex ->
            // to find a cluster of overlapping segments search forward first until there is a gap
            let loc1 = expandCluster nextIndex Upwards lines
            // now, using the (larger) union of bounds fond searching forward, search backwards. This may find
            // extra lines due to larger bound and in any case will search at least a little way beyond the initial
            // start - enough to see if there is a second barrier.
            // note that every segment can only be grouped once, so this search will not pick up previously clustered
            // segments when searching backwards.
            let loc2 = expandCluster (List.max loc1.Segments) (Downwards loc1) lines

            match loc2 with
            | { Segments = lowestLoc2Index :: _
                LowerFix = lowerFix } when lines[lowestLoc2Index].P > lines[nextIndex].P ->
                List.except loc2.Segments loc1.Segments
                |> (fun segs ->
                    if segs = [] then
                        [ loc2 ]
                    else
                        if not <| List.contains nextIndex segs then
                            failwithf "What? nextIndex has got lost from loc1!"

                        segs
                        |> (fun segs ->
                            { loc1 with
                                Segments = segs
                                UpperFix = lowerFix })
                        |> (fun loc -> expandCluster (List.max loc.Segments) (Downwards loc) lines)
                        |> (fun loc1 ->
                            (if not <| List.contains nextIndex loc1.Segments then
                                    failwithf "What? nextIndex has got lost from loc1 after expansion!")

                            loc1)
                        |> (fun loc1 -> [ loc2; loc1 ]))
            | _ ->
                (if not <| List.contains nextIndex loc2.Segments then
                        failwithf "What? nextIndex has got lost from loc2!")

                [ loc2 ]
            |> List.map keepOnlyGroupableSegments
            |> List.filter (fun loc -> loc.Segments <> [])
            |> (fun newLocs ->
                    newLocs
                    |> List.iter (fun loc -> 
                        //printf "%s" (pAllCluster lines loc)
                        loc.Segments |> List.iter (fun seg -> groupableA[seg] <- false))

                    if groupable nextIndex then
                        failwithf "Error: infinite loop detected in cluster find code"

                    newLocs @ getClusters lines)

    getClusters lines

// Currently not used. Running the algorithm twice fixes problems otherwise needing merge (and other things).
// Should decide what is an acceptable space between merged clusters so as not to move
// segments too far.
/// Return single cluster with segments from loc1 and loc2 merged
let mergeLocs (lines: Line array) (loc1: Cluster) (loc2: Cluster) =
    if upperB lines loc1 < lowerB lines loc2 || not (hasOverlap loc1.Bound loc2.Bound) then
        [ loc1; loc2 ] // do not merge
    else
        // Bound and SearchStart fields are no longer used.
        // printf $"Merging:\n{pAllCluster lines loc1}\n{pAllCluster lines loc2}"

        [ { loc1 with
                UpperFix = loc2.UpperFix
                Segments = loc1.Segments @ loc2.Segments } ]

/// Currently not used.
/// Go through the list of clusters merging where possible, return merged list.
/// lines is array of Lines from which clusters are generated
let mergeLocalities (lines: Line array) (locL: Cluster list) =
    let rec merge (mergedLocs: Cluster list) (locL: Cluster list) =
        match mergedLocs, locL with
        | mLocs, [] -> mLocs // no clusters to merge!
        | [], loc :: locs -> merge [ loc ] locs
        | currLoc :: mLocL, loc :: locL ->
            match currLoc.UpperFix with
            | Some upperB -> merge (loc :: currLoc :: mLocL) locL
            | None -> merge (mergeLocs lines currLoc loc @ mLocL) locL

    merge [] locL

/// Function which given a cluster (loc) works out how to
/// spread out the contained segments optimally, spacing them from other segments and symbols.
/// Return value is a list of segments, represented as Lines, paired with where they move.
/// lines is the source list of lines (vertical or horizontal according to which is being processed).
/// model is the Buswire model needed to access wires.
let calcSegPositions model lines loc =
    let segs = loc.Segments |> List.distinct |> orderToMinimiseCrossings model lines
    // if segs.Length > 1 then
    // printfn $"** Grouping: {segs |> List.map (fun i -> i, lines[i].P)} **"
    let pts = segs |> List.map (fun i -> lines[i].P)
    let nSeg = loc.Segments.Length

    let spreadFromStart start sep =
        //printfn $"spread: %.2f{start}: %.2f{sep} {segs} {loc.UpperFix} {loc.LowerFix}"
        segs |> List.mapi (fun i seg -> lines[seg], start + sep * float i)

    let spreadFromMiddle mid sep =
        segs
        |> List.mapi (fun i seg -> lines[seg], mid + sep * float i - float (nSeg - 1) * sep / 2.)

    let spreadFromEnd endP sep =
        segs |> List.mapi (fun i seg -> lines[seg], endP + sep * float (i - (nSeg - 1)))

    let maxSep = maxSegmentSeparation
    let halfMaxSep = maxSegmentSeparation / 2.
    let idealMidpoint = (List.min pts + List.max pts) / 2.
    let halfIdealWidth = float (nSeg - 1) * halfMaxSep

    let idealStart, idealEnd =
        idealMidpoint - halfIdealWidth, idealMidpoint + halfIdealWidth
    // Fixed bounds and soft segment bounds behave differently
    // Segments are placed maxSegmentSeparation away from fixed bound but only halfSep away from soft bounds
    match loc.UpperFix, loc.LowerFix, nSeg with
    | None, None, 1 -> [] // no change
    | Some bMax, Some bMin, n when (bMax - bMin) / (float n + 1.) < maxSep ->
        //printf $"spread {nSeg} constrained"
        spreadFromMiddle ((bMax + bMin) / 2.) ((bMax - bMin) / (float n + 1.))
    | _, Some bMin, _ when bMin + maxSep > idealStart ->
        //printf $"spread {nSeg} from start"
        spreadFromStart (bMin + maxSep) maxSep
    | Some bMax, _, n when bMax - maxSep < idealEnd ->
        //printf $"spread {nSeg} from end - endP={bMax-maxSep}"
        spreadFromEnd (bMax - maxSep) maxSep
    | bMax, bMin, n ->
        //printf $"spread {nSeg} from middle bmax= {bMax}, bMin={bMin}"
        spreadFromMiddle idealMidpoint maxSep


/// Given a list of segment changes of given orientation apply them to the model
let adjustSegmentsInModel (ori: Orientation) (model: Model) (changes: (Line * float) list) =
    let changes =
        changes 
        |> List.collect (fun (line, p) ->
            if line.SameNetLink = [] then 
                [line,p]
            else
                line.SameNetLink |> List.iteri (fun i lin -> printfn $"{line.Lid.Index}({i}): Linked net: {lin.Lid.Index},{lin.P} -> {p}")
                [(line,p)] @ (line.SameNetLink |> List.map (fun line2 -> line2,p)))
    let wires =
        (model.Wires, changes)
        ||> List.fold (fun wires (line, newP) ->
            let seg = Option.get line.Seg1
            moveLine ori newP line wires)

    Optic.set wires_ wires model

/// Segments which could be moved, but would make an extra segment if moved, are marked Fixed
/// and not moved by the normal cluster-based separation functions.
/// This function looks at these segments and moves them a little in the special case that they
/// overlap. It is called after the main segment separation is complete.
let separateFixedSegments (ori: Orientation) (model: Model) =
    /// direction from line which has maximum available P space, up to maxOffset,
    /// Return value is space available - negative if more space is in negative direction.
    let getSpacefromLine (lines: Line array) (line: Line) (excludeLine: Line) (maxOffset: float) =
        let p = line.P
        let find offset dir = 
            tryFindIndexInArray 
                (LineId(line.Lid.Index + dir)) 
                dir 
                (fun line2 -> hasOverlap line2.B line.B && line2.Lid <> excludeLine.Lid ) 
                (fun l1 -> abs (l1.P - p) > 2. * offset) 
                lines
        match find maxOffset 1, find maxOffset -1 with
        | None, _ -> maxOffset
        | _, None -> -maxOffset
        | Some a, Some b -> 
            if abs (lines[a.Index].P - p) > abs (lines[b.Index].P - p) then 
                lines[a.Index].P - p
            else 
                lines[b.Index].P - p

    makeLines ori model
    |> (fun lines -> 
        Array.pairwise lines
        |> Array.filter (fun (line1, line2) -> 
                line1.LType = FIXEDSEG && line2.LType = FIXEDSEG &&
                abs (line1.P - line2.P) < overlapTolerance &&
                line1.PortId <> line2.PortId &&
                hasOverlap line1.B line2.B)
        |> Array.map (fun (line1, line2) ->
            let space1 = getSpacefromLine lines line1 line2 2*maxSegmentSeparation
            let space2 = getSpacefromLine lines line2 line1 2*maxSegmentSeparation
            if abs space1 > abs space2 then
                line1, line1.P + space1 * 0.5
            else
                line2, line1.P + space2 * 0.5)
        |> List.ofArray)
    |> adjustSegmentsInModel ori model
    
//-------------------------------------------------------------------------------------------------//
//--------------------------------------WIRE ARTIFACT CLEANUP--------------------------------------//
//-------------------------------------------------------------------------------------------------//
(*
    The segment-based optimisations can sometimes leave wires in a non-optimal state with too many
    corners. This code scans down each 9 segment wire and attempts to remove redundant corners:

    ----              ------           ------               ----
        |      ==>          |                |         ===>     |
        ---                 |              ---                  |
            |                 |              |                    |
    
    Note that these two cases are the same: two consecutive turns are removed and a 3rd turn is moved 
    as required to keep wires joining.

    The optimised wire can be accepted as long as 
    (1) it does not go inside or too close to symbols
    (2) it does not go too close to other wires.

*)
/// Return the index of the Line with the smallest value of P > p
/// Use binary earch for speed.
let findInterval (lines: Line array) ( p: float): int =
    let rec find above below =
        if above - below < 2 then above
        else
            let mid = (above + below) / 2
            if lines[mid].P < p then
                find above mid
            else
                find mid below
    find (lines.Length - 1) 0

/// Return true if there is no overlap between line and lines array (with exception of excludedLine).
/// All lines are the same type (parallel)
let checkExtensionNoOverlap 
        (overlap: float) 
        (ext: Extension)
        (excludedWire: ConnectionId) 
        (info: LineInfo) : bool =
    let lines =
        match ext.ExtOri with
        | Horizontal -> info.HLines
        | Vertical -> info.VLines
    let b = ext.ExtB
    let p = ext.ExtP
    let iMin = findInterval lines (p - overlap)
    let rec check i =
        if i >= lines.Length || i < 0  || lines[i].P > p + overlap then 
            true
        elif lines[i].Wid = excludedWire || not (hasNearOverlap overlap b lines[i].B) then
            check (i+1)
        else
            false
    check iMin
    |> (fun x -> printf $"No overlap: {x}"; x)


/// Return true if there is no crossing symbol boundary between line 
/// and lines array (with exception of excludedLine).
/// Lines and excludedLine or opposite orientation from line
let checkExtensionNoCrossings 
        (overlap: float) 
        (ext: Extension)
        (excludedWire: ConnectionId) 
        (info: LineInfo) : bool =

    let lines =
        match ext.ExtOri with
        | Horizontal -> info.VLines
        | Vertical -> info.HLines
    let b = ext.ExtB
    let p = ext.ExtP
    let iMin = findInterval lines (b.MinB - overlap)
    let rec check i =
        let otherLine = lines[i]
        let b = otherLine.B
        if i >= lines.Length || i < 0 || otherLine.P > b.MaxB + overlap then 
            true
        elif lines[i].Wid = excludedWire || b.MinB > p || b.MaxB < p || not (lines[i].LType = FIXED) then
            check (i+1)
        else
            printf $"cross: {pLine lines[i]} p={p} b={b}"
            false
    check iMin
    |> (fun x -> printf $"no cross: Line:{x} p={p} b = {b}"; x)


/// Process the symbols and wires in Model generating arrays of Horizontal and Vertical lines.
/// In addition the inverse map is generated which can map each segmnet to the corresponding Line if that
/// exists.
/// Note that Lines reference segments, which contain wire Id and segment Index and can therefore be used to
/// reference the corresponding wire via the model.Wires map.
let makeLineInfo (model:Model) : LineInfo =
    
        let hLines = makeLines Horizontal model
        let vLines = makeLines Vertical model
        let wireMap = model.Wires
        let lineMap =
            Array.append hLines vLines
            |> Array.collect (fun line -> 
                match line.Seg1 with
                | None -> [||]
                | Some aSeg -> 
                    [| aSeg.Segment.GetId(), line.Lid |] )
            |> Map.ofArray
        {
            HLines = hLines
            VLines = vLines
            WireMap = wireMap
            LineMap = lineMap
        }
    
/// Return true if the given segment length change is allowed.
/// If the new segment creates a part line segment
/// that did not previouly exist this is checked for overlap
/// with symbols and other wires.
let isSegmentExtensionOk
        (info: LineInfo)
        (wire: Wire)
        (start: int)
        (ori: Orientation)
        (lengthChange: float)
            : bool =
    let seg = wire.Segments[start]
    let len = seg.Length
    let aSegStart, aSegEnd = getAbsoluteSegmentPos wire start
    let p, endC, startC =
        match ori with
        | Vertical -> aSegStart.X, aSegEnd.Y, aSegStart.Y
        | Horizontal -> aSegStart.Y, aSegEnd.X, aSegStart.X
    printf "isOK: %s" (pWire wire)
    /// check there is room for the proposed segment extension
    let extensionhasRoomOnSheet b1 b2 =
        let extension = {ExtP = p; ExtOri = ori; ExtB = {MinB = min b1 b2; MaxB = max b1 b2}}
        checkExtensionNoOverlap extensionTolerance extension wire.WId info &&
        checkExtensionNoCrossings extensionTolerance extension wire.WId info


    match sign (lengthChange + len) = sign len, abs (lengthChange + len) < abs len with
    | true, true ->
        true // nothing to do because line is made shorter.
    | true,false ->
        //printfn $"lengthChange={lengthChange} len={len}"
        extensionhasRoomOnSheet (endC+lengthChange) endC
    | false, _ ->
        if not (segmentIsNubExtension wire start) then
            //printf $"allowing start={start} wire={pWire wire}"
            extensionhasRoomOnSheet (endC+lengthChange) startC                
        else
            false

/// Return the list of wire corners found in given wire with all corner
/// edges smaller than cornerSizeLimit. A wire can have at most one corner.
let findWireCorner (info: LineInfo) (cornerSizeLimit: float) (wire:Wire): WireCorner list =
    let segs = wire.Segments
    let nSegs = wire.Segments.Length
    printf $"Find: {pWire wire}"
    let pickStartOfCorner (start:int) =
        printf $"Pick (start={start}): {pWire wire}"
        let seg = segs[start]    
        if segs[start].IsZero() || segs[start+3].IsZero() then 
            printf "zero seg - cancelled"
            None
        else
            let deletedSeg1,deletedSeg2 = segs[start+1], segs[start+2]
            let hasManualSegment = List.exists (fun i -> segs[i].Mode = Manual) [start..start+3]
            let hasLongSegment = max (abs deletedSeg1.Length) (abs deletedSeg2.Length) > cornerSizeLimit
            if hasManualSegment || hasLongSegment then 
                printf "manual or long - cancelled"
                None
            else
                let ori = wire.InitialOrientation
                let startSegOrientation = if seg.Index % 2 = 0 then ori else switchOrientation ori
                let change1 = deletedSeg2.Length
                let change2 = deletedSeg1.Length
                if isSegmentExtensionOk info wire start ori change1 &&
                    isSegmentExtensionOk info wire (start+3)  (switchOrientation ori) change2
                then
                    {
                        Wire = wire
                        StartSeg = start
                        StartSegOrientation = startSegOrientation
                        StartSegChange = change1
                        EndSegChange = change2
                    } |> Some
                else
                    None
                        

    // Wire corners cannot start on zero-length segments (that would introduce
    // an extra bend). The 4 segments changed by the corner cannot be manually
    // routed.
    [1..nSegs-5]
    |> List.tryPick pickStartOfCorner
    |> function | None -> [] | Some x -> [x]

/// Change LineInfo removing a corner from a wire.
/// TODO: currently only WireMap changes
let removeCorner (info: LineInfo) (wc: WireCorner): LineInfo =
    let removeSegments start num (segments: Segment list) =
        segments
        |> List.removeManyAt start num
        |> (List.mapi (fun i seg -> if i > start - 1 then {seg with Index = i} else seg))

    printf $"**Removing corner: visible nub={getVisibleNubLength false wc.Wire}, {getVisibleNubLength true wc.Wire} **"
    let addLengthToSegment (delta:float) (seg: Segment)=
        {seg with Length = seg.Length + delta}
    let wire' = 
        wc.Wire.Segments
        |> List.updateAt wc.StartSeg (addLengthToSegment wc.StartSegChange wc.Wire.Segments[wc.StartSeg])
        |> List.updateAt (wc.StartSeg + 3) (addLengthToSegment wc.EndSegChange wc.Wire.Segments[wc.StartSeg + 3])
        |> removeSegments (wc.StartSeg+1) 2
        |> (fun segs -> {wc.Wire with Segments = segs})
    {info with WireMap = Map.add wire'.WId wire' info.WireMap}

/// Return model with corners identified and removed where possible. 
/// Corners are artifacts - usually small - which give wires more visible segments than is needed.
let removeModelCorners (model: Model) =
    let info = makeLineInfo model
    printf $"H:\n{pLines info.HLines}"
    printf $"H:\n{pLines info.HLines}"

    let wires = model.Wires
    let corners =
        wires
        |> Map.values
        |> Seq.toList
        |> List.collect (findWireCorner info maxCornerSize)
    (info, corners)
    ||> List.fold removeCorner
    |> (fun info' -> Optic.set wires_ info'.WireMap model)       
    
/// Return None, or Some wire' where wire' is wire with spikes removed.
/// Spikes segments that turn back on previous ones (with a zero-length segment in between).
/// Optimised for the case that there are no spikes and None is returned.
let removeWireSpikes (wire: Wire) : Wire option =
    let segs = wire.Segments
    (None, segs)
    ||> List.fold (fun segsOpt seg ->
        let n = seg.Index
        let segs = Option.defaultValue segs segsOpt
        let nSeg = segs.Length
        if n > nSeg - 3 || not (segs[n+1].IsZero()) || sign segs[n].Length = sign segs[n+2].Length then 
            segsOpt
        else
            let newSegN = {segs[n] with Length = segs[n].Length + segs[n+2].Length}
            let lastSegs = 
                segs[n+3..nSeg-1]
                    
            [
                segs[0..n-1]
                [newSegN]
                (List.mapi (fun i seg -> {seg with Index = i + n + 1}) lastSegs)
            ]
            |> List.concat
            |> Some)  
    |> Option.map (fun segs ->
            printf $"Despiked wire {pWire wire}"
            {wire with Segments = segs})

/// return model with all wire spikes removed
let removeModelSpikes (model: Model) =
    printf "Removing spikes"
    (model.Wires, model.Wires)
    ||> Map.fold (fun wires wid wire ->
        match removeWireSpikes wire with
        | None -> wires
        | Some wire' -> Map.add wid wire' wires)
    |> (fun wires -> {model with Wires = wires})


//-------------------------------------------------------------------------------------------------//
//----------------------------------------TOP LEVEL FUNCTIONS--------------------------------------//
//-------------------------------------------------------------------------------------------------//

/// Perform complete segment ordering and separation for segments of given orientation.
let separateModelSegmentsOneOrientation (ori: Orientation) (model: Model) =
    makeLines ori model
    |> fun lines ->
        makeClusters lines
        //|> List.map (fun p -> (printf "%s" (pAllCluster lines p)); p)
        //|> mergeLocalities lines // merging does not seem necessary?
        |> List.collect (calcSegPositions model lines)
    |> adjustSegmentsInModel ori model
    //|> removeModelSpikes

/// Perform complete segment separation and ordering for all orientations
let separateAndOrderModelSegments (wiresToRoute: ConnectionId list) =
    separateModelSegmentsOneOrientation Vertical
    >> separateModelSegmentsOneOrientation Horizontal
    >> separateModelSegmentsOneOrientation Vertical // repeat vertical separation since moved segments may now group
    >> separateModelSegmentsOneOrientation Horizontal // as above
    >> separateFixedSegments Vertical 
    >> separateFixedSegments Horizontal
    >> removeModelCorners
    >> removeModelSpikes


