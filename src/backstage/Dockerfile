# Production image for Spotify Backstage
# Built using a multi-stage Docker build to eliminate host machine dependencies.

# Stage 1: Build the Backstage backend bundle
FROM node:22-bookworm-slim AS builder

RUN apt-get update && \
    apt-get install -y --no-install-recommends \
      libsqlite3-dev python3 g++ build-essential && \
    rm -rf /var/lib/apt/lists/*

USER node
WORKDIR /app

# Copy yarn configuration
COPY --chown=node:node .yarn ./.yarn
COPY --chown=node:node .yarnrc.yml package.json yarn.lock backstage.json ./

# Copy package configurations
COPY --chown=node:node packages/app/package.json ./packages/app/package.json
COPY --chown=node:node packages/backend/package.json ./packages/backend/package.json

# Copy all source files
COPY --chown=node:node packages ./packages
COPY --chown=node:node plugins ./plugins
COPY --chown=node:node tsconfig.json ./
COPY --chown=node:node app-config*.yaml ./

# Install dependencies and build bundle
RUN yarn install
RUN yarn tsc
RUN yarn build:backend

# Stage 2: Create the minimal production runner image
FROM node:22-bookworm-slim AS runner

# Install runtime native module dependencies (libsqlite3 for better-sqlite3)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
      libsqlite3-dev python3 g++ build-essential && \
    rm -rf /var/lib/apt/lists/*

# Run as least-privileged 'node' user
USER node
WORKDIR /app

# Copy yarn configuration
COPY --chown=node:node .yarn ./.yarn
COPY --chown=node:node .yarnrc.yml package.json yarn.lock backstage.json ./

# Production mode - only production dependencies will be installed
ENV NODE_ENV=production
ENV NODE_OPTIONS="--no-node-snapshot"

# Copy the built skeleton and bundle from the builder stage
COPY --chown=node:node --from=builder /app/packages/backend/dist/skeleton.tar.gz ./
RUN tar xzf skeleton.tar.gz && rm skeleton.tar.gz

RUN yarn workspaces focus --all --production && rm -rf "$(yarn cache clean)"

# Copy example catalog files
COPY --chown=node:node examples ./examples

# Copy the full backend bundle and app configs
COPY --chown=node:node --from=builder /app/packages/backend/dist/bundle.tar.gz ./
COPY --chown=node:node app-config*.yaml ./
RUN tar xzf bundle.tar.gz && rm bundle.tar.gz

ENTRYPOINT ["node", "packages/backend", "--config", "app-config.yaml", "--config", "app-config.production.yaml"]
