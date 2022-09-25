window.BENCHMARK_DATA = {
  "lastUpdate": 1664148383578,
  "repoUrl": "https://github.com/chgl/vfps",
  "entries": {
    "PseudonymGeneratorBenchmarks": [
      {
        "commit": {
          "author": {
            "email": "chgl@users.noreply.github.com",
            "name": "chgl",
            "username": "chgl"
          },
          "committer": {
            "email": "chgl@users.noreply.github.com",
            "name": "chgl",
            "username": "chgl"
          },
          "distinct": true,
          "id": "0b50b22059070fa8b8b86609f7b4ceb25ca0a825",
          "message": "ci: give benchmark job write access to repo",
          "timestamp": "2022-09-26T01:24:51+02:00",
          "tree_id": "13a0b67751241ee7a31974006a8f65710a9c966c",
          "url": "https://github.com/chgl/vfps/commit/0b50b22059070fa8b8b86609f7b4ceb25ca0a825"
        },
        "date": 1664148383012,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "Vfps.Benchmarks.PseudonymGeneratorBenchmarks.CryptoRandomBase64UrlEncodedGenerator",
            "value": 1794.5043131510417,
            "unit": "ns",
            "range": "± 1.1442632447638423"
          },
          {
            "name": "Vfps.Benchmarks.PseudonymGeneratorBenchmarks.HexEncodedSha256HashGenerator",
            "value": 1328.9973123990571,
            "unit": "ns",
            "range": "± 0.677253033784542"
          }
        ]
      }
    ]
  }
}