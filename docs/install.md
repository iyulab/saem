# Installing saem on one machine

This installs saem — the MCP server, the administration console, one executor for each of two groups — beside a small forms
source (MorphDB and Formbase) and a development issuer, with a sample to try them on. Everything is built from this source tree.

**It is for evaluation.** The development issuer mints a token for anyone who asks, and the console is served over plain HTTP on the
machine's loopback address. Read *What this install does not promise* before putting it anywhere else.

## What you need

Docker with Compose v2, and git. Nothing else is installed on the machine: the hosts are built in containers. The first start
downloads base images and builds the console's screens, so it needs the network and takes a few minutes.

## Install

```sh
git clone https://github.com/iyulab/saem && cd saem
sh deploy/install.sh                                          # writes deploy/.env with generated secrets
docker compose -f deploy/compose.yaml up -d --build --wait    # every service reports healthy
```

`deploy/.env` holds the ports and every secret, generated on the spot; keep it private. `install.sh` refuses to overwrite it.

## Sign in

Open <http://localhost:58081>. The console sends you to the development issuer at `http://issuer.localhost:58080` and back; it
signs you in as `pat`, a builder in the group `g-plant`. Your browser reads `*.localhost` as this machine; if yours does not,
add `127.0.0.1 issuer.localhost` to your hosts file. Change the ports in `deploy/.env` before the first start if they are taken.

## Try the sample

The install files sixteen documents into the forms source (items and work orders, filed by `alice`, `bob`, `olive`, `pat` and
`nobody`) and leaves a sample catalog waiting as a draft. Some rows carry a `CANARY` value: a mark that must reach no one who may
not read its row.

1. In the console, open **Draft** and press **Confirm the draft**: that puts the sample catalog in force.
2. Open **Groups** and, for `g-plant` and for `g-office`, press **Refresh from the source**. Each group reads the source through an
   account of its own and keeps its edges in a store of its own.
3. Run the check; it asks the questions the sample's answers are known for, as the people the sample names:

   ```sh
   docker compose -f deploy/compose.yaml run --rm check
   ```

   It ends with `check: all pass`. `--change` also corrects and retires a filed row and checks the changed-rows pass against a
   whole one; it changes the sample, so run it once and last.

To ask the MCP server yourself, take a token for a person from the issuer — `POST http://issuer.localhost:58080/rig/token` with
`grant_type=client_credentials`, `client_id=<person>`, `audience=saem`, `scope=saem-rig` — and call the `lookup` tool at
`http://localhost:58082/mcp`.

## Connect a model (optional)

The sample works without one. To draft an ontology from the filed forms, add three lines to `deploy/.env`:

```
SAEM_MODEL_ENDPOINT=https://your-model-host/v1/
SAEM_MODEL=your-model-name
SAEM_MODEL_API_KEY=...
```

and run `docker compose -f deploy/compose.yaml run --rm author`. Up to 20 documents of each form type are sent to that endpoint,
and to nothing else. The draft it writes replaces the sample draft and waits for a person to confirm it in the console. The model drafts only what the catalog in force does not declare yet: run it before confirming the sample, or it has little to add.

## Stop, start, remove

```sh
docker compose -f deploy/compose.yaml stop            # keeps the data; `up -d --wait` starts it again
docker compose -f deploy/compose.yaml down -v         # removes the containers, the networks and every volume
rm deploy/.env                                        # then `sh deploy/install.sh` makes a new install
```

## Replace a secret

Each group's executor accepts the secret the console presents to it. To replace `g-plant`'s, put a new value in `deploy/.env` as
`SAEM_G_PLANT_EXECUTOR_SECRET` (24 or more characters; for example `od -An -tx1 -N24 /dev/urandom | tr -d ' \n'`) and run
`docker compose -f deploy/compose.yaml up -d executor-g-plant console`. The old value is then refused by the executor.

## What this install does not promise

- **A production identity.** The issuer accepts any client and any secret; connect your own, as *Deploying the console* in the
  README describes, and serve the console over HTTPS.
- **One machine, one instance of each host.** Several instances need a shared key ring for the console's sessions and affinity
  or a refresh-token reuse window at the issuer (see the README).
- **A deployment topology** beyond this compose file, **how long data is kept or how it is deleted**, and **how a catalog changes
  between versions** of saem. None of these is designed here.
