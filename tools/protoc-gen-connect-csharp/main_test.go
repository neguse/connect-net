package main

import (
	"testing"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/descriptorpb"
	"google.golang.org/protobuf/types/pluginpb"
)

func TestGenerateWithoutGoPackage(t *testing.T) {
	message := func(name string) *descriptorpb.DescriptorProto {
		return &descriptorpb.DescriptorProto{Name: proto.String(name)}
	}
	resp := generate(&pluginpb.CodeGeneratorRequest{
		FileToGenerate: []string{"greeter.proto"},
		ProtoFile: []*descriptorpb.FileDescriptorProto{{
			Name:        proto.String("greeter.proto"),
			Package:     proto.String("example"),
			Syntax:      proto.String("proto3"),
			MessageType: []*descriptorpb.DescriptorProto{message("HelloRequest"), message("HelloResponse")},
			Service: []*descriptorpb.ServiceDescriptorProto{{
				Name: proto.String("GreeterService"),
				Method: []*descriptorpb.MethodDescriptorProto{{
					Name:       proto.String("SayHello"),
					InputType:  proto.String(".example.HelloRequest"),
					OutputType: proto.String(".example.HelloResponse"),
				}},
			}},
		}},
	})
	if resp.Error != nil {
		t.Fatal(*resp.Error)
	}
	got := map[string]bool{}
	for _, file := range resp.File {
		got[file.GetName()] = true
	}
	for _, want := range []string{"GreeterService.connect.cs", "greeter.connect-json.cs"} {
		if !got[want] {
			t.Errorf("missing %s in %v", want, got)
		}
	}
}
