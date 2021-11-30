#!/bin/bash

cd ~

if [ -d "Documents/workspace/microting/eform-debian-service/Plugins/ServiceTimePlanningPlugin" ]; then
	rm -fR Documents/workspace/microting/eform-debian-service/Plugins/ServiceTimePlanningPlugin
fi

mkdir Documents/workspace/microting/eform-debian-service/Plugins

cp -av Documents/workspace/microting/eform-service-timeplanning-plugin/ServiceTimePlanningPlugin Documents/workspace/microting/eform-debian-service/Plugins/ServiceTimePlanningPlugin
