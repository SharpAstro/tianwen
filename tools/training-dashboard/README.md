# training-dashboard

A local page over every long run on this machine: trainings, bakes, checks and test suites. For each run it shows
whether it is alive and what it is costing (CPU, memory, GPU), how far it has got, where its data lives and how much
room that drive has left, and what it is FOR: the question it answers, its setup, its pre-registered predictions and
kill line, and its caveats. Below the runs are every scored star remover's numbers, each metric explained, and any
other heavy process on the box.

```
pip install psutil
python tools/training-dashboard/server.py --runs C:/temp/e2/dashboard/runs.json
```

then open http://127.0.0.1:8765 (`--port` to change it). The page refreshes every 5 s, and the registry and the
glossary are re-read on every refresh, so a run is added or explained by editing JSON, with no restart.

## It only reads

It starts, stops and writes nothing but its own `server.pid`, written beside the registry. It reads processes
(psutil), the GPU (`nvidia-smi`; the page still serves without one), each run's status file, the last 16 KiB of each
console log (opened shared, so a job appending to it is never blocked) and the starless-eval reports. It never stops a
run: a run's stop file is shown, and creating it is left to whoever owns the run.

It binds to 127.0.0.1 only.

## The registry

`runs.json` (copy `runs.example.json`; git ignores `runs.json` here, since its paths and PIDs are one machine's). Per
run:

| field | what |
|---|---|
| `tag` | the short name scripts use |
| `title` | what it is in plain words; the card's heading |
| `kind` | `training`, `bake`, `check`, `eval`, `tests` or anything else; shown as a chip |
| `what`, `question`, `setup`, `predictions` (a list), `kill`, `caveat` | the explanation the card shows; write a run's predictions here as it is pre-registered, and its answer into `predictions` once it is read |
| `pid` | a Windows PID, for a run started once and watched to its end |
| `match` + `exe` | otherwise: substrings every one of which the command line holds (the newest such process wins), and the process name it must start with, so a wrapper shell whose command line names the run never stands in for it |
| `status`, `log`, `stop`, `result` | files: a status line (`done`, `failed` and `stopped` end a run), a console log (only ever tail-read), the stop file the run honours, and the file whose existence means it has been read |
| `datastore` | folders the run reads or writes; each shows whether it exists and its drive's free space |
| `progress` | `{"type": "steps"}`: `step N/M ... tiles/s ... elapsed X min` lines in the log, with the best validation loss and an ETA to the step cap; `{"type": "count", "pattern": regex, "total": n}`: matching lines in the log; `{"type": "files", "glob": ..., "total": n}`: files that exist; `{"type": "lastline"}`: the log's last line |

`scores` lists starless-eval report globs (`starless-eval-v4.json`), each a table on the page; a model's name is its
report's folder (the one above `on-random`, when there is one). `watch` names the process-name prefixes the "other
processes" table shows when one uses more than 1 % of a core or 0.5 GB.

A run's state is `running` while its process lives, else its status file's first word, else `done` once its result
exists or its progress reaches its total, else `waiting`.

## The glossary

`explain.json` (`--explain` for another) is what the page explains with: `programme` (a title and paragraphs on what
is being trained and how it is judged), `metrics` (each score column's label, which direction is better, and what it
measures) and `models` (a regex on a model's name and what that arm was). It is the star remover's today
(`docs/plans/star-remover-training.md`); a new arm gets a `models` entry when it is pre-registered.
