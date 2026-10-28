# Static build served by nginx. Build args are the PUBLIC Supabase values.
FROM node:22-alpine AS build
WORKDIR /app
COPY web/package.json web/package-lock.json ./web/
RUN cd web && npm ci
COPY web ./web
COPY supabase/functions/drive/core.ts ./supabase/functions/drive/core.ts
ARG VITE_SUPABASE_URL
ARG VITE_SUPABASE_ANON_KEY
ARG VITE_DRIVE_ENABLED=true
RUN cd web && npm run build

FROM nginx:1.27-alpine
COPY web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/web/dist /usr/share/nginx/html
EXPOSE 8080
