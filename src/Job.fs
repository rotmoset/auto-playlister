﻿
module Job

open System
open System.Text.RegularExpressions
open FSpotify
open RedditSharp
open System.Collections.Generic

let inline isNull (x:^T when ^T : not struct) = obj.ReferenceEquals (x, null)

type Parameters = {
    Playlist: {| User: SpotifyId; Id: SpotifyId; Limit: int|}
    Subreddit: {| Name: string; Pattern: string; Limit: int|}
}
    

type Submission = {
    Artist: string
    Title: string
}

module AsyncEnumerator =
    let rec ToSeq<'a> (enumerator: IAsyncEnumerator<'a>) = seq {
        let maybeAnElement =
            async {
                let! yes = enumerator.MoveNext() |> Async.AwaitTask 
                if yes then return Some enumerator.Current else return None
            } |> Async.RunSynchronously
        match maybeAnElement with
        | Some element ->
            yield element
            yield! ToSeq<'a> enumerator
        | None ->
            ()
    }


let submissionFeed (reddit: Reddit) subreddit regex  =
    let regex = new Regex(regex)
    let trim (str: string) = str.Trim ()
    fun length ->
        async {
            let! subreddit = reddit.GetSubredditAsync(subreddit) |> Async.AwaitTask

            return  
                subreddit.GetPosts(Things.Subreddit.Sort.Hot, length).GetEnumerator()
                |> AsyncEnumerator.ToSeq
                |> Seq.choose (fun submission ->
                    
                    let ``match`` = regex.Match submission.Title

                    if ``match``.Success then  
                        { Artist = trim ``match``.Groups.["artist"].Value
                          Title = trim ``match``.Groups.["title"].Value } |> Some
                    else
                        printfn "Did not match submission pattern: %s" submission.Title
                        None
                )
        }

let search =
    
    let agent = MailboxProcessor.Start (fun mailbox ->
        let rec search (cacheAge: DateTime) (cache: Map<Submission,Track option>) = async {
            
            if (DateTime.Now - cacheAge).TotalDays > 1.0 then
                printfn "Clearing old search cache"
                return! search DateTime.Now Map.empty

            let! ((song,reply): Submission*AsyncReplyChannel<Track option>) = mailbox.Receive ()
            match cache |> Map.tryFind song with
            | Some trackIdResult ->
                trackIdResult |> reply.Reply
                return! search cacheAge cache
            | None ->
                let! trackResult =
                    Authenticator.withAuthentication (fun token -> async {
                            return
                                sprintf "track:\"%s\" artist:\"%s\"" song.Title song.Artist
                                |> Search.Query
                                |> FSpotify.Search.track
                                |> Request.withOptionals (fun o -> {o with limit = Some 1})
                                |> Request.withAuthorization token
                                |> Paging.page
                                |> Paging.asSeq
                                |> Seq.tryPick Some     
                        }                     
                    )

                trackResult |> reply.Reply
                return! cache |> Map.add song trackResult |> search cacheAge 
        }

        Misc.supervise <| search DateTime.Now Map.empty 
    )

    fun song -> agent.PostAndAsyncReply (fun reply -> song,reply)


module Playlist =

    type Item = {Track: Track; Added: DateTime}
    
    type Action =
        | Exists of Track*AsyncReplyChannel<bool>
        | Add of Track
        | Remove of Track
        | Count of AsyncReplyChannel<int>
        | Tracks of AsyncReplyChannel<Item list>

    type Agent = MailboxProcessor<Action>

    let exists track (agent: Agent) = agent.PostAndAsyncReply (fun reply -> Exists(track, reply))

    let add track (agent: Agent) = agent.Post (Add track)

    let count (agent: Agent) = agent.PostAndAsyncReply Count

    let remove track (agent: Agent) = agent.Post (Remove track)

    let tracks (agent: Agent) = agent.PostAndAsyncReply Tracks

    let load userId playlistId: Agent =

        let loadTracks = 
            Authenticator.withAuthentication (fun token -> async {
                return
                    Playlist.tracks userId playlistId
                    |> Request.withAuthorization token
                    |> Paging.page
                    |> Paging.asSeq                
            })

        let removeTrack (track: Track) =
            Authenticator.withAuthentication (fun token ->
                Playlist.removeTracks userId playlistId [track.id]
                |> Request.withAuthorization token
                |> Request.mapResponse ignore
                |> Request.asyncSend
            )

        let addTrack (track: Track) =
            Authenticator.withAuthentication (fun token ->
                FSpotify.Playlist.add userId playlistId [track.id]
                |> Request.withAuthorization token
                |> Request.mapResponse ignore
                |> Request.asyncSend                
            )

        let asCache (tracks: PlaylistTrack seq) =
            tracks 
            |> Seq.filter (fun track -> track.track |> isNull |> not)
            |> Seq.map (fun track -> track.track.id, {Track = track.track; Added = track.added_at})
            |> Map.ofSeq

        MailboxProcessor.Start(fun mailbox ->
            let rec listen (cache: Map<SpotifyId,Item>) = async {
                let! message = mailbox.Receive ()
                match message with
                | Exists (track, reply) ->
                    cache |> Map.containsKey track.id |> reply.Reply
                    return! listen cache
                | Add track ->
                    do! addTrack track
                    return! cache |> Map.add track.id {Track = track; Added = DateTime.Now} |> listen
                | Remove track ->
                    do! removeTrack track
                    return! cache |> Map.remove track.id |> listen
                | Count reply ->
                    reply.Reply cache.Count
                    return! listen cache
                | Tracks reply ->
                    cache
                    |> Map.toList
                    |> List.map snd
                    |> reply.Reply
                    return! listen cache

            }
            async {
                let! existing = loadTracks
                return! existing |> asCache |> listen
            } |> Misc.supervise
        )

let run reddit (parameters: Parameters) =
    let stream = submissionFeed reddit parameters.Subreddit.Name parameters.Subreddit.Pattern

    let playlist = Playlist.load parameters.Playlist.User parameters.Playlist.Id

    let rec trimPlaylist () = async {
        let! count = playlist |> Playlist.count
        if count > parameters.Playlist.Limit then
            let! tracks = playlist |> Playlist.tracks
            let oldest = tracks |> Seq.minBy (fun item -> item.Added)
            printfn "Playlist limit reached. Removing track '%s'" oldest.Track.name
            do playlist |> Playlist.remove oldest.Track
            do! trimPlaylist ()
    }

    let rec run () = async {
        let! songs = stream parameters.Subreddit.Limit

        for song in songs do

            let! track = search song

            match track with
            | Some track ->
                let! exists = playlist |> Playlist.exists track
                match exists with
                | true ->
                    printfn "Skipping: '%s' -- '%s' as it's already added to playlist" song.Artist song.Title
                | false ->
                    printfn "Adding: '%s' -- '%s' to playlist" song.Artist song.Title
                    do playlist |> Playlist.add track
                    do! trimPlaylist ()
            | None ->
                printfn "Skipping: '%s' -- '%s' as it's not available on spotify" song.Artist song.Title

    }

    run ()

