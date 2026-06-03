ARG npm_baseimage_tag
FROM $npm_baseimage_tag

USER root

COPY apache-conf/pingheader.conf /etc/httpd/conf.d/
COPY apache-conf/rewrite.conf    /etc/httpd/conf.d/
COPY apache-conf/rproxy.conf     /etc/httpd/conf.d/
COPY apache-conf/robots.txt      /opt/rh/httpd24/root/var/www/html/

RUN mkdir -p /var/www/html/

# Angular SPA build output. The build script writes to dist/dse/ by convention
# (see angular.json `outputPath`); keep that in sync if the project is renamed.
COPY dist/dse/ /var/www/html/

# Reset permissions of filesystem to default values
RUN chown -R 1001:0 /var/www/html && \
    chmod -R "g+rwx" /var/www/html && \
    rpm-file-permissions

EXPOSE 8080 8443

USER 1001

ENTRYPOINT ["/usr/bin/run-httpd"]
