﻿using System.Text.Json.Serialization;

namespace TempusDemoArchive.Jobs.StvProcessor;

public record User(
    [property: JsonPropertyName("classes")]
    Classes Classes,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("userId")] int? UserId,
    [property: JsonPropertyName("steamId")]
    string SteamId,
    [property: JsonPropertyName("team")] string Team
);

public record Chat(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tick")] int? Tick
);

public record Classes(
    [property: JsonPropertyName("0")] int? _0,
    [property: JsonPropertyName("3")] int? _3,
    [property: JsonPropertyName("9")] int? _9,
    [property: JsonPropertyName("4")] int? _4,
    [property: JsonPropertyName("1")] int? _1,
    [property: JsonPropertyName("7")] int? _7
);

public record Death(
    [property: JsonPropertyName("weapon")] string Weapon,
    [property: JsonPropertyName("victim")] int? Victim,
    [property: JsonPropertyName("assister")]
    object Assister,
    [property: JsonPropertyName("killer")] int? Killer,
    [property: JsonPropertyName("tick")] int? Tick
);

public record Header(
    [property: JsonPropertyName("demo_type")]
    string DemoType,
    [property: JsonPropertyName("version")]
    int? Version,
    [property: JsonPropertyName("protocol")]
    int? Protocol,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("nick")] string Nick,
    [property: JsonPropertyName("map")] string Map,
    [property: JsonPropertyName("game")] string Game,
    [property: JsonPropertyName("duration")]
    double? Duration,
    [property: JsonPropertyName("ticks")] int? Ticks,
    [property: JsonPropertyName("frames")] int? Frames,
    [property: JsonPropertyName("signon")] int? Signon
);

public record StvParserResponse(
    [property: JsonPropertyName("header")] Header Header,
    [property: JsonPropertyName("chat")] IReadOnlyList<Chat> Chat,
    [property: JsonPropertyName("users")] Dictionary<string, User> Users,
    [property: JsonPropertyName("deaths")] IReadOnlyList<Death> Deaths,
    [property: JsonPropertyName("rounds")] IReadOnlyList<object> Rounds,
    [property: JsonPropertyName("startTick")]
    int? StartTick,
    [property: JsonPropertyName("intervalPerTick")]
    double? IntervalPerTick,
    [property: JsonPropertyName("pauses")] IReadOnlyList<object> Pauses
);