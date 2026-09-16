FROM python:3.12-alpine
ARG WORKLOAD_REVISION
ENV WORKLOAD_REVISION=${WORKLOAD_REVISION}
LABEL io.honua.devops.proof=protected-recovery
COPY server.py /srv/server.py
EXPOSE 8080
CMD ["python", "/srv/server.py"]
